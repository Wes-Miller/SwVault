using System.Collections.Concurrent;
using SwVault.Core;
using SwVault.Core.Client;
using SwVault.Core.Lfs;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>
/// Runs PDM operations for clients. Workspace-changing jobs stop at ReadyToApply when files
/// would be replaced, so the add-in can release documents open in SOLIDWORKS before job.apply.
/// </summary>
internal sealed class JobManager
{
    private sealed class JobEntry
    {
        public required JobInfo Info { get; init; }
        public required VaultSession Session { get; init; }
        public StagedOperation? Staged { get; set; }
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private readonly VaultManager _vaults;
    private readonly Action<string, object> _broadcast;
    private readonly FileLog _log;

    public JobManager(VaultManager vaults, Action<string, object> broadcast, FileLog log)
    {
        _vaults = vaults;
        _broadcast = broadcast;
        _log = log;
    }

    public async Task<JobInfo> StartAsync(JobRequest request)
    {
        var paths = request.Paths ?? Array.Empty<string>();
        var session = await _vaults.ResolveAsync(request.VaultId, paths.FirstOrDefault()).ConfigureAwait(false);
        var entry = new JobEntry
        {
            Session = session,
            Info = new JobInfo { JobId = Guid.NewGuid().ToString("N"), VaultId = session.VaultId, Kind = request.Kind, State = JobState.Running, Tag = request.Tag },
        };
        _jobs[entry.Info.JobId] = entry;
        _log.Info($"Job {entry.Info.JobId[..8]} {request.Kind} on {paths.Length} path(s) in {session.VaultId}");
        Publish(entry);

        var progress = ThrottledProgress(entry);
        var ct = entry.Cts.Token;
        try
        {
            switch (request.Kind)
            {
                case JobKind.GetLatest:
                    entry.Staged = await session.PrepareGetLatestAsync(paths.Length == 0 ? new[] { session.LocalRoot } : paths, request.WithReferences, progress, ct).ConfigureAwait(false);
                    break;
                case JobKind.CheckOut:
                    entry.Staged = await session.CheckOutAsync(paths, takeOver: request.Force, forTransition: request.TransitionName, progress, ct).ConfigureAwait(false);
                    break;
                case JobKind.UndoCheckOut:
                    entry.Staged = await session.PrepareUndoCheckOutAsync(paths, discardChanges: request.Force, progress, ct).ConfigureAwait(false);
                    break;
                case JobKind.GetVersion:
                    entry.Staged = await session.PrepareGetVersionAsync(paths[0], request.Version, request.AsBuilt, request.Force, progress, ct).ConfigureAwait(false);
                    break;
                case JobKind.Rollback:
                    entry.Staged = await session.PrepareRollbackAsync(paths[0], request.Version, progress, ct).ConfigureAwait(false);
                    break;
                case JobKind.CheckIn:
                case JobKind.Import:
                {
                    var result = await session.CheckInAsync(paths, request.Files, new CheckInOptions
                    {
                        Comment = request.Comment,
                        KeepCheckedOut = request.KeepCheckedOut,
                        TransitionName = request.TransitionName,
                        IsImport = request.Kind == JobKind.Import,
                        AllowMissingReferences = request.Kind == JobKind.Import || request.Force,
                    }, progress, ct).ConfigureAwait(false);
                    Complete(entry, result.Commit, paths, result.Warnings,
                        result.NewVersions.Count == 0 ? "No content changes; check-outs released." : $"Checked in {string.Join(", ", result.NewVersions)}");
                    return entry.Info;
                }
                case JobKind.Transition:
                {
                    var (commit, warnings) = await session.TransitionAsync(paths, request.TransitionName ?? throw VaultException.BadRequest("Missing transition name."), request.Comment, ct).ConfigureAwait(false);
                    Complete(entry, commit, paths, warnings, $"{request.TransitionName} done.");
                    return entry.Info;
                }
                case JobKind.Delete:
                {
                    var (commit, warnings) = await session.DeleteAsync(paths, request.Comment, request.Force, ct).ConfigureAwait(false);
                    Complete(entry, commit, paths, warnings, "Deleted.");
                    return entry.Info;
                }
                default:
                    throw VaultException.BadRequest($"Unsupported job kind {request.Kind}.");
            }

            var staged = entry.Staged!;
            entry.Info.Warnings = staged.Warnings.Count > 0 ? staged.Warnings.ToArray() : null;
            entry.Info.Affected = staged.AffectedLocalPaths.ToArray();
            var replacements = staged.Replacements;
            if (replacements.Count == 0 || request.AutoApply)
                return await ApplyAsync(entry.Info.JobId).ConfigureAwait(false);

            entry.Info.State = JobState.ReadyToApply;
            entry.Info.Replacements = replacements.ToArray();
            entry.Info.Message = $"{replacements.Count} file(s) ready to replace.";
            Publish(entry);
            return entry.Info;
        }
        catch (Exception ex)
        {
            Fail(entry, ex);
            return entry.Info;
        }
    }

    public async Task<JobInfo> ApplyAsync(string jobId, IReadOnlyCollection<string>? skipPaths = null)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) throw VaultException.NotFound($"Unknown job {jobId}.");
        var staged = entry.Staged ?? throw VaultException.BadRequest("This job has nothing to apply.");
        try
        {
            entry.Info.State = JobState.Applying;
            Publish(entry);
            var skipped = new List<string>();
            if (skipPaths is { Count: > 0 })
            {
                var skip = new HashSet<string>(skipPaths, StringComparer.OrdinalIgnoreCase);
                skipped.AddRange(staged.Changes.Where(c => skip.Contains(c.LocalPath)).Select(c => c.LocalPath));
                staged.Changes.RemoveAll(c => skip.Contains(c.LocalPath));
            }
            var result = await entry.Session.ApplyAsync(staged, entry.Cts.Token).ConfigureAwait(false);
            skipped.AddRange(result.Skipped);
            entry.Info.State = JobState.Completed;
            entry.Info.Progress = 1;
            entry.Info.Skipped = skipped.Count > 0 ? skipped.ToArray() : null;
            entry.Info.Affected = staged.AffectedLocalPaths.ToArray();
            var warnings = staged.Warnings.Concat(result.Warnings).ToArray();
            entry.Info.Warnings = warnings.Length > 0 ? warnings : null;
            entry.Info.Message = skipped.Count > 0
                ? $"{result.Applied.Count} file(s) updated; {skipped.Count} were in use and skipped."
                : $"{result.Applied.Count} file(s) updated.";
            entry.Staged = null;
            Publish(entry);
            _broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = entry.Session.VaultId, Paths = entry.Info.Affected });
            Forget(entry);
            return entry.Info;
        }
        catch (Exception ex)
        {
            Fail(entry, ex);
            return entry.Info;
        }
    }

    public JobInfo Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) throw VaultException.NotFound($"Unknown job {jobId}.");
        entry.Cts.Cancel();
        if (entry.Staged?.StagingDir is { } dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
        entry.Info.State = JobState.Cancelled;
        Publish(entry);
        Forget(entry);
        return entry.Info;
    }

    public JobInfo Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var entry) ? entry.Info : throw VaultException.NotFound($"Unknown job {jobId}.");

    private void Complete(JobEntry entry, string? commit, IReadOnlyList<string> paths, IReadOnlyList<string> warnings, string message)
    {
        entry.Info.State = JobState.Completed;
        entry.Info.Progress = 1;
        entry.Info.Commit = commit;
        entry.Info.Affected = paths.ToArray();
        entry.Info.Warnings = warnings.Count > 0 ? warnings.ToArray() : null;
        entry.Info.Message = message;
        Publish(entry);
        _broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = entry.Session.VaultId, Paths = entry.Info.Affected, FullRefresh = paths.Count == 0 });
        Forget(entry);
    }

    private void Fail(JobEntry entry, Exception ex)
    {
        entry.Info.State = JobState.Failed;
        if (ex is VaultException vex)
        {
            entry.Info.ErrorCode = vex.Code;
            entry.Info.Error = vex.Message;
            _log.Warn($"Job {entry.Info.JobId[..8]} {entry.Info.Kind} failed: {vex.Message}");
        }
        else if (ex is OperationCanceledException)
        {
            entry.Info.State = JobState.Cancelled;
            entry.Info.Error = "Cancelled.";
        }
        else
        {
            entry.Info.ErrorCode = ErrorCodes.Internal;
            entry.Info.Error = "Unexpected error: " + ex.Message;
            _log.Error($"Job {entry.Info.JobId[..8]} {entry.Info.Kind} crashed", ex);
        }
        Publish(entry);
        Forget(entry);
    }

    private void Forget(JobEntry entry)
    {
        // Keep finished jobs briefly so late job.get calls still find them.
        _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => _jobs.TryRemove(entry.Info.JobId, out JobEntry? _), TaskScheduler.Default);
    }

    private void Publish(JobEntry entry) => _broadcast(Notifications.JobUpdated, entry.Info);

    private IProgress<TransferProgress> ThrottledProgress(JobEntry entry)
    {
        var last = DateTime.MinValue;
        return new Progress<TransferProgress>(p =>
        {
            entry.Info.Progress = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
            entry.Info.Message = $"{p.FilesDone}/{p.FilesTotal} files";
            if ((DateTime.UtcNow - last).TotalMilliseconds < 300 && p.FilesDone < p.FilesTotal) return;
            last = DateTime.UtcNow;
            Publish(entry);
        });
    }
}
