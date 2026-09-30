using System.Text;
using SwVault.Core.Git;
using SwVault.Core.Index;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed partial class VaultSession
{
    /// <summary>Makes every file LFS-stored and lockable for plain git-lfs users too; SwVault's own files stay text.</summary>
    public const string GitAttributes =
        "# Managed by SwVault. All vault files are stored in Git LFS and are lockable (check-out = LFS lock).\n" +
        "* filter=lfs diff=lfs merge=lfs -text lockable\n" +
        ".gitattributes -filter -diff -merge text -lockable\n" +
        ".swvault/** -filter -diff -merge text -lockable\n";

    internal sealed record VersionEntry(CommitInfo Commit, FileMeta Meta);

    // ---------------------------------------------------------------- history

    public async Task<IReadOnlyList<VersionInfoDto>> GetHistoryAsync(string path, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        await TrySyncAsync(refreshLocks: false, ct).ConfigureAwait(false);
        var entries = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return entries.Select(e => new VersionInfoDto
        {
            Version = e.Meta.Version,
            Commit = e.Commit.Sha,
            Oid = e.Meta.Oid,
            Size = e.Meta.Size,
            By = e.Commit.AuthorName.Length > 0 ? e.Meta.CheckedInBy ?? e.Commit.AuthorName : e.Meta.CheckedInBy,
            At = e.Commit.Date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            Comment = e.Commit.Subject,
            State = e.Meta.State,
            Revision = e.Meta.Revision,
        }).ToList();
    }

    internal async Task<IReadOnlyList<VersionEntry>> LoadHistoryAsync(string vaultPath, CancellationToken ct)
    {
        var canonical = _head.Get(vaultPath)?.Path ?? PathRules.Normalize(vaultPath);
        var metaPath = PathRules.MetaPathFor(canonical);
        var list = new List<VersionEntry>();
        foreach (var commit in await Mirror.LogPathAsync(metaPath, 1000, ct).ConfigureAwait(false))
        {
            var bytes = await Mirror.ReadBlobAsync($"{commit.Sha}:{metaPath}", ct).ConfigureAwait(false);
            if (bytes == null) continue; // the commit that deleted the file
            var meta = HeadIndex.TryParseMeta(Encoding.UTF8.GetString(bytes));
            if (meta != null && meta.Oid.Length > 0) list.Add(new VersionEntry(commit, meta));
        }
        if (list.Count > 0) return list;

        // Added outside SwVault (no sidecar): derive versions from the pointer's own history.
        var commits = await Mirror.LogPathAsync(canonical, 1000, ct).ConfigureAwait(false);
        for (var i = 0; i < commits.Count; i++)
        {
            var bytes = await Mirror.ReadBlobAsync($"{commits[i].Sha}:{canonical}", ct).ConfigureAwait(false);
            if (bytes == null || !LfsPointer.TryParse(bytes, out var pointer)) continue;
            list.Add(new VersionEntry(commits[i], new FileMeta
            {
                Version = commits.Count - i,
                Oid = pointer.Oid,
                Size = pointer.Size,
                CheckedInBy = commits[i].AuthorName,
                CheckedInAt = commits[i].Date.ToString("O"),
            }));
        }
        return list;
    }

    internal async Task<VersionEntry> FindVersionAsync(string vaultPath, int version, CancellationToken ct)
    {
        var history = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return history.FirstOrDefault(h => h.Meta.Version == version)
            ?? throw VaultException.NotFound($"Version {version} of {vaultPath} was not found.");
    }

    internal async Task<long> SizeOfVersionAsync(string vaultPath, int version, string oid, CancellationToken ct)
    {
        var head = _head.Get(vaultPath);
        if (head?.Oid == oid) return head.Size;
        var history = await LoadHistoryAsync(vaultPath, ct).ConfigureAwait(false);
        return history.FirstOrDefault(h => h.Meta.Oid == oid)?.Meta.Size ?? -1;
    }

    // ---------------------------------------------------------------- references

    public async Task<ReferenceNodeDto?> GetReferenceTreeAsync(string path, bool recursive, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(path);
        if (vaultPath == null) return null;
        var visited = new HashSet<string>(PathRules.Comparer);

        async Task<ReferenceNodeDto> BuildAsync(string p, int version, int depth)
        {
            var head = _head.Get(p);
            var canonical = head?.Path ?? p;
            var (state, record, _) = await ComputeLocalStateAsync(canonical, head, ct).ConfigureAwait(false);
            var node = new ReferenceNodeDto
            {
                Path = canonical,
                LocalPath = LocalPathOf(canonical),
                Version = version,
                Status = BuildStatus(canonical, head, state, record),
            };
            if (!visited.Add(canonical) || depth > 40) return node;
            var refs = head?.Meta?.References;
            if (refs != null && (recursive || depth == 0))
            {
                var children = new List<ReferenceNodeDto>();
                foreach (var r in refs) children.Add(await BuildAsync(r.Path, r.Version, depth + 1).ConfigureAwait(false));
                node.Children = children.ToArray();
            }
            return node;
        }

        return await BuildAsync(vaultPath, _head.Get(vaultPath)?.Version ?? 0, 0).ConfigureAwait(false);
    }

    public IReadOnlyList<string> WhereUsed(string path)
    {
        var vaultPath = ResolveVaultPath(path);
        return vaultPath != null && _head.WhereUsed.TryGetValue(vaultPath, out var parents) ? parents : Array.Empty<string>();
    }

    // ---------------------------------------------------------------- workflow

    public async Task<IReadOnlyList<TransitionOptionDto>> GetTransitionsAsync(string path, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path);
        var head = vaultPath == null ? null : _head.Get(vaultPath);
        if (head == null) return Array.Empty<TransitionOptionDto>();
        return WorkflowEngine.Options(Config, head, user.Login, _head).Select(c => new TransitionOptionDto
        {
            Name = c.Transition.Name,
            To = c.Transition.To,
            BumpRevision = c.Transition.BumpRevision,
            NextRevision = c.NextRevision,
            Exports = c.Transition.Exports.ToArray(),
            ExportFolder = LocalPathOf(PathRules.Normalize(Config.ReleaseExportFolder.Replace("{folder}", PathRules.GetFolder(head.Path), StringComparison.OrdinalIgnoreCase))),
            Allowed = c.Allowed,
            Reason = c.Reason,
            Warnings = c.Warnings.Count > 0 ? c.Warnings.ToArray() : null,
        }).ToList();
    }

    /// <summary>
    /// Metadata-only state change (submit, reject, change request, or a release that doesn't need to
    /// write into the file). Releases that stamp the revision into the file go through CheckInAsync
    /// with a TransitionName instead.
    /// </summary>
    public async Task<(string? Commit, IReadOnlyList<string> Warnings)> TransitionAsync(
        IReadOnlyList<string> paths, string transitionName, string? comment, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var items = new List<(HeadFile Head, TransitionCheck Check, bool HeldBefore)>();
            foreach (var raw in paths.Distinct(PathRules.Comparer))
            {
                var vaultPath = ResolveVaultPath(raw);
                var head = vaultPath == null ? null : _head.Get(vaultPath);
                if (head == null) throw VaultException.NotFound($"{raw} is not in the vault.");
                var record = State.GetFile(head.Path);
                var lockState = LockStateOf(head.Path, record, out var lockRecord);
                if (lockState == LockState.Other) throw VaultException.Conflict($"{head.Path} is checked out by {lockRecord!.Owner}.");
                if (lockState == LockState.MineElsewhere) throw VaultException.Conflict($"{head.Path} is checked out by you on another computer.");
                if (lockState == LockState.MineHere)
                {
                    var (state, _, _) = await ComputeLocalStateAsync(head.Path, head, ct).ConfigureAwait(false);
                    if (state != LocalState.UpToDate) throw VaultException.Conflict($"{head.Path} has changes that aren't checked in yet. Check it in first.");
                }
                var check = WorkflowEngine.Evaluate(Config, head, transitionName, user.Login, _head);
                if (!check.Allowed) throw VaultException.Forbidden($"{head.Path}: {check.Reason}");
                warnings.AddRange(check.Warnings);
                items.Add((head, check, lockState == LockState.MineHere));
            }

            var tempLocks = new List<string>();
            try
            {
                foreach (var item in items.Where(i => !i.HeldBefore))
                {
                    var created = await Locks.CreateAsync(item.Head.Path, ct).ConfigureAwait(false);
                    if (!created.Success) throw VaultException.Conflict($"{item.Head.Path} was just checked out by {created.Existing?.OwnerName}.");
                    SetLock(new LockRecord(created.Created!.Id, item.Head.Path, user.Login, created.Created.LockedAt, true));
                    tempLocks.Add(item.Head.Path);
                }

                var message = string.IsNullOrWhiteSpace(comment)
                    ? $"{transitionName}: {string.Join(", ", items.Select(i => PathRules.GetFileName(i.Head.Path)).Take(5))}{(items.Count > 5 ? "..." : "")}"
                    : comment!.Trim();
                message += $"\n\nWorkflow: {transitionName}\n";
                var now = NowIso();
                var commit = await CommitWithRetryAsync(head =>
                {
                    var plan = new CommitPlan();
                    foreach (var item in items)
                    {
                        var current = head.Get(item.Head.Path);
                        if (current == null || current.Oid != item.Head.Oid)
                            throw VaultException.Conflict($"{item.Head.Path} changed on the server; refresh and try again.");
                        var meta = current.Meta?.Clone() ?? new FileMeta { Version = current.Version, Oid = current.Oid, Size = current.Size };
                        meta.State = item.Check.Transition.To;
                        if (item.Check.Transition.BumpRevision)
                        {
                            meta.Revision = item.Check.NextRevision;
                            meta.RevisionHistory ??= new List<RevisionEntry>();
                            meta.RevisionHistory.Add(new RevisionEntry { Rev = meta.Revision!, Version = meta.Version, By = user.Login, At = now });
                        }
                        plan.Writes.Add((PathRules.MetaPathFor(current.Path), Encoding.UTF8.GetBytes(Json.Serialize(meta) + "\n")));
                    }
                    return plan;
                }, message, user, ct).ConfigureAwait(false);

                // A file moving into a read-only state (e.g. InReview) can't stay checked out.
                if (Config.IsReadOnlyState(items.FirstOrDefault().Check?.Transition.To))
                    foreach (var item in items.Where(i => i.HeldBefore)) await ReleaseAsync(item.Head.Path, ct).ConfigureAwait(false);
                return (commit, warnings);
            }
            finally
            {
                foreach (var path in tempLocks)
                {
                    try { await ReleaseAsync(path, CancellationToken.None).ConfigureAwait(false); }
                    catch (VaultException ex) { warnings.Add($"Could not release the temporary lock on {path}: {ex.Message}"); }
                }
            }
        }
        finally
        {
            _opGate.Release();
        }
    }

    // ---------------------------------------------------------------- delete & admin

    public async Task<(string? Commit, IReadOnlyList<string> Warnings)> DeleteAsync(
        IReadOnlyList<string> paths, string? comment, bool force, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var targets = ExpandTargets(paths, withReferences: false, warnings);
            if (targets.Count == 0) throw VaultException.NotFound("Nothing to delete.");
            if (force && !Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can delete files that other files still use.");

            var targetSet = new HashSet<string>(targets.Select(t => t.Path), PathRules.Comparer);
            foreach (var target in targets)
            {
                var parents = WhereUsed(target.Path).Where(p => !targetSet.Contains(p)).ToList();
                if (parents.Count > 0 && !force)
                    throw VaultException.Conflict($"{target.Path} is used by {string.Join(", ", parents.Take(5))}{(parents.Count > 5 ? "..." : "")}. Remove those references first.");
                if (LockStateOf(target.Path, State.GetFile(target.Path), out var l) == LockState.Other)
                    throw VaultException.Conflict($"{target.Path} is checked out by {l!.Owner}.");
            }

            foreach (var target in targets.Where(t => LockOf(t.Path) == null))
            {
                var created = await Locks.CreateAsync(target.Path, ct).ConfigureAwait(false);
                if (!created.Success) throw VaultException.Conflict($"{target.Path} was just checked out by {created.Existing?.OwnerName}.");
                SetLock(new LockRecord(created.Created!.Id, target.Path, user.Login, created.Created.LockedAt, true));
            }

            var message = (string.IsNullOrWhiteSpace(comment) ? $"Delete {targets.Count} file(s)" : comment!.Trim())
                + "\n\nDeleted:\n" + string.Join("\n", targets.Take(50).Select(t => "  " + t.Path)) + "\n";
            var commit = await CommitWithRetryAsync(head =>
            {
                var plan = new CommitPlan();
                foreach (var target in targets)
                {
                    var current = head.Get(target.Path);
                    if (current == null) continue;
                    plan.Deletes.Add(current.Path);
                    if (current.Meta != null) plan.Deletes.Add(PathRules.MetaPathFor(current.Path));
                }
                return plan;
            }, message, user, ct).ConfigureAwait(false);

            foreach (var target in targets)
            {
                var lockRecord = LockOf(target.Path);
                if (lockRecord is { Mine: true })
                {
                    await Locks.UnlockAsync(lockRecord.Id, force: false, ct).ConfigureAwait(false);
                    ClearLock(target.Path);
                }
                var local = LocalPathOf(target.Path);
                var record = State.GetFile(target.Path);
                if (File.Exists(local))
                {
                    var current = await CurrentOidAsync(target.Path, new FileInfo(local), record, ct).ConfigureAwait(false);
                    if (current == target.Oid)
                    {
                        SetReadOnlyAttribute(local, false);
                        File.Delete(local);
                    }
                    else
                    {
                        warnings.Add($"{target.Path} had local changes, so your local copy was kept.");
                    }
                }
                State.DeleteFile(target.Path);
            }
            return (commit, warnings);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>Admin: release someone else's check-out (e.g. they left for the semester with files checked out).</summary>
    public async Task ForceUnlockAsync(string path, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(ct).ConfigureAwait(false);
        if (!Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can release other people's check-outs.");
        await SyncAsync(fetch: false, refreshLocks: true, ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        var lockRecord = LockOf(vaultPath) ?? throw VaultException.NotFound($"{vaultPath} is not checked out.");
        await Locks.UnlockAsync(lockRecord.Id, force: !lockRecord.Mine, ct).ConfigureAwait(false);
        ClearLock(vaultPath);
    }

    /// <summary>First commit of a new vault: .gitattributes and .swvault/vault.json.</summary>
    public async Task<string?> InitializeAsync(VaultConfig config, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            if (_head.HasConfig) throw VaultException.Conflict("This vault is already initialized.");
            return await CommitWithRetryAsync(head =>
            {
                if (head.HasConfig) throw VaultException.Conflict("This vault is already initialized.");
                var plan = new CommitPlan();
                plan.Writes.Add((PathRules.GitAttributesPath, Encoding.UTF8.GetBytes(GitAttributes)));
                plan.Writes.Add((PathRules.ConfigPath, Encoding.UTF8.GetBytes(Json.Serialize(config) + "\n")));
                return plan;
            }, $"Initialize vault '{config.Name}'", user, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task<string?> UpdateConfigAsync(VaultConfig config, string? comment, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            if (_head.HasConfig && !Config.IsAdmin(user.Login)) throw VaultException.Forbidden("Only vault admins can change vault settings.");
            return await CommitWithRetryAsync(_ =>
            {
                var plan = new CommitPlan();
                plan.Writes.Add((PathRules.ConfigPath, Encoding.UTF8.GetBytes(Json.Serialize(config) + "\n")));
                return plan;
            }, string.IsNullOrWhiteSpace(comment) ? "Update vault settings" : comment!, user, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }
}
