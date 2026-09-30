using System.Text;
using SwVault.Core.Git;
using SwVault.Core.Hosts;
using SwVault.Core.Index;
using SwVault.Core.Lfs;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed record CheckInOptions
{
    public string? Comment { get; init; }
    public bool KeepCheckedOut { get; init; }

    /// <summary>Workflow transition applied in the same commit (e.g. "Approve" after writing the revision into the file).</summary>
    public string? TransitionName { get; init; }

    /// <summary>Bulk import: new files only, no locks, committed in batches, already-imported files skipped.</summary>
    public bool IsImport { get; init; }
    public bool AllowMissingReferences { get; init; }
}

public sealed record CheckInResult(string? Commit, IReadOnlyList<string> NewVersions, IReadOnlyList<string> Released, IReadOnlyList<string> Warnings);

/// <summary>Blobs to write and paths to delete for one commit attempt.</summary>
internal sealed class CommitPlan
{
    public List<(string Path, byte[] Content)> Writes { get; } = new();
    public List<string> Deletes { get; } = new();
    public bool IsEmpty => Writes.Count == 0 && Deletes.Count == 0;
}

public sealed partial class VaultSession
{
    private const int ImportBatchFiles = 500;
    private const long ImportBatchBytes = 5L * 1024 * 1024 * 1024;

    private sealed class CheckInItem
    {
        public required string Path { get; init; }
        public required string LocalPath { get; init; }
        public string? BaseOid { get; init; }
        public bool IsNew { get; init; }
        public CheckInFileInfo? Info { get; init; }
        public string Oid { get; set; } = "";
        public long Size { get; set; }
        public bool Changed { get; set; }
        public TransitionCheck? Transition { get; set; }
        public int NewVersion { get; set; }
    }

    public async Task<CheckInResult> CheckInAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<CheckInFileInfo>? infos,
        CheckInOptions options,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var warnings = new List<string>();
            var infoByPath = new Dictionary<string, CheckInFileInfo>(PathRules.Comparer);
            foreach (var info in infos ?? Array.Empty<CheckInFileInfo>())
            {
                var vp = info.LocalPath == null ? null : ResolveVaultPath(info.LocalPath);
                if (vp != null) infoByPath[vp] = info;
            }

            var items = await CollectItemsAsync(paths, infoByPath, options, user, warnings, ct).ConfigureAwait(false);
            if (items.Count == 0) return new CheckInResult(null, Array.Empty<string>(), Array.Empty<string>(), warnings);

            if (options.IsImport)
            {
                string? last = null;
                var versions = new List<string>();
                foreach (var batch in Batches(items))
                {
                    await Lfs.UploadAsync(batch.Where(i => i.Changed).Select(i => new LfsUploadItem(i.Oid, i.Size, i.LocalPath)).ToList(), progress, ct).ConfigureAwait(false);
                    var batchCommit = await CommitItemsAsync(batch, options, user, warnings, ct).ConfigureAwait(false);
                    if (batchCommit == null) continue;
                    last = batchCommit;
                    foreach (var item in batch)
                    {
                        RecordCheckedIn(item.Path, item.Oid, item.Size, item.NewVersion, batchCommit);
                        SetReadOnlyAttribute(item.LocalPath, true);
                        versions.Add($"{item.Path} v{item.NewVersion}");
                    }
                }
                return new CheckInResult(last, versions, Array.Empty<string>(), warnings);
            }

            // New files get a lock too, so two people can't add the same path at once.
            foreach (var item in items.Where(i => i.IsNew))
            {
                var created = await Locks.CreateAsync(item.Path, ct).ConfigureAwait(false);
                var mine = created.Success || string.Equals(created.Existing?.OwnerName, user.Login, StringComparison.OrdinalIgnoreCase);
                if (!mine) throw VaultException.Conflict($"{created.Existing?.OwnerName ?? "Someone else"} is adding {item.Path} right now.");
                var l = created.Created ?? created.Existing!;
                SetLock(new LockRecord(l.Id, item.Path, user.Login, l.LockedAt, true));
                MarkLockedHere(item.Path, true);
            }

            await Lfs.UploadAsync(items.Where(i => i.Changed).Select(i => new LfsUploadItem(i.Oid, i.Size, i.LocalPath)).ToList(), progress, ct).ConfigureAwait(false);

            var verified = await Locks.VerifyAsync(ct).ConfigureAwait(false)
                ?? throw VaultException.Forbidden("You don't have write access to this vault.");
            var ours = new HashSet<string>(verified.Ours.Select(l => PathRules.Normalize(l.Path)), PathRules.Comparer);
            var lost = items.Where(i => !ours.Contains(i.Path)).Select(i => i.Path).ToList();
            if (lost.Count > 0)
                throw VaultException.Conflict("You no longer hold the check-out for: " + string.Join(", ", lost) + ". An admin may have released it; save your changes as a copy.");

            var commit = await CommitItemsAsync(items, options, user, warnings, ct).ConfigureAwait(false);

            foreach (var item in items.Where(i => i.Changed)) RecordCheckedIn(item.Path, item.Oid, item.Size, item.NewVersion, commit!);

            var released = new List<string>();
            if (!options.KeepCheckedOut)
            {
                var journal = new JobRecord(Guid.NewGuid().ToString("N"), "unlock", "pending",
                    Json.Serialize(items.Select(i => i.Path).ToList()));
                State.PutJob(journal);
                foreach (var item in items)
                {
                    await ReleaseAsync(item.Path, ct).ConfigureAwait(false);
                    released.Add(item.Path);
                }
                State.RemoveJob(journal.Id);
            }

            var newVersions = items.Where(i => i.Changed).Select(i => $"{i.Path} v{i.NewVersion}").ToList();
            return new CheckInResult(commit, newVersions, released, warnings);
        }
        finally
        {
            _opGate.Release();
        }
    }

    private async Task<List<CheckInItem>> CollectItemsAsync(
        IReadOnlyList<string> paths, Dictionary<string, CheckInFileInfo> infoByPath, CheckInOptions options,
        HostUser user, List<string> warnings, CancellationToken ct)
    {
        var items = new List<CheckInItem>();
        var seen = new HashSet<string>(PathRules.Comparer);
        foreach (var raw in paths)
        {
            var vaultPath = ResolveVaultPath(raw) ?? throw VaultException.BadRequest($"{raw} is outside the vault folder {LocalRoot}.");
            if (IsIgnored(vaultPath))
            {
                warnings.Add($"{vaultPath} matches the vault's ignore list and was skipped.");
                continue;
            }
            var head = _head.Get(vaultPath);
            var canonical = head?.Path ?? vaultPath;
            if (!seen.Add(canonical)) continue;
            var local = LocalPathOf(canonical);
            if (!File.Exists(local)) throw VaultException.NotFound($"{canonical} doesn't exist in your vault folder.");
            infoByPath.TryGetValue(canonical, out var info);
            var (oid, size) = await Hashing.Sha256FileAsync(local, ct).ConfigureAwait(false);

            if (head == null)
            {
                items.Add(new CheckInItem { Path = canonical, LocalPath = local, IsNew = true, Info = info, Oid = oid, Size = size, Changed = true });
                continue;
            }

            if (options.IsImport)
            {
                if (oid != head.Oid) warnings.Add($"{canonical} already exists in the vault with different content; skipped.");
                continue; // already imported (e.g. resuming an interrupted import)
            }

            var record = State.GetFile(canonical);
            switch (LockStateOf(canonical, record, out var lockRecord))
            {
                case LockState.None:
                    throw VaultException.Conflict($"{canonical} is not checked out. Check it out first.");
                case LockState.Other:
                    throw VaultException.Conflict($"{canonical} is checked out by {lockRecord!.Owner}.");
                case LockState.MineElsewhere:
                    throw VaultException.Conflict($"{canonical} is checked out by you on another computer; check it in from there.");
            }

            var (state, rec, _) = await ComputeLocalStateAsync(canonical, head, ct).ConfigureAwait(false);
            if (state is LocalState.Conflict or LocalState.Outdated || rec?.BaseOid != head.Oid)
                throw VaultException.Conflict($"{canonical} changed on the server after you got it, so your copy can't be checked in over it. Save your work as a copy.");
            items.Add(new CheckInItem { Path = canonical, LocalPath = local, BaseOid = head.Oid, Info = info, Oid = oid, Size = size, Changed = oid != head.Oid });
        }

        var guard = Config.SolidworksVersion;
        if (!string.IsNullOrEmpty(guard))
        {
            var mismatched = items.Where(i => !string.IsNullOrEmpty(i.Info?.SwVersion) && !i.Info!.SwVersion!.StartsWith(guard, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mismatched.Count > 0)
                throw VaultException.Conflict($"This vault is on SOLIDWORKS {guard} but these files were saved with {mismatched[0].Info!.SwVersion}: {string.Join(", ", mismatched.Select(i => i.Path))}. Saving with a newer version would make them unreadable for teammates.");
        }

        if (options.TransitionName != null)
        {
            foreach (var item in items.Where(i => !i.IsNew))
            {
                var check = WorkflowEngine.Evaluate(Config, _head.Get(item.Path)!, options.TransitionName, user.Login, _head);
                if (!check.Allowed) throw VaultException.Forbidden($"{item.Path}: {check.Reason}");
                warnings.AddRange(check.Warnings);
                item.Transition = check;
            }

            // New files in a transition check-in are generated exports (PDF/STEP): they enter the target
            // state directly with the same revision as the files being released.
            var transition = Config.Workflow.Transitions.FirstOrDefault(t => string.Equals(t.Name, options.TransitionName, StringComparison.OrdinalIgnoreCase))
                ?? throw VaultException.BadRequest($"Unknown workflow transition '{options.TransitionName}'.");
            var revision = items.Select(i => i.Transition?.NextRevision).FirstOrDefault(r => r != null)
                ?? (transition.BumpRevision ? Revisions.Next(null, Config.Workflow) : null);
            foreach (var item in items.Where(i => i.IsNew))
            {
                if (!transition.Roles.Any(r => Config.UserHasRole(user.Login, r)))
                    throw VaultException.Forbidden($"'{transition.Name}' requires role {string.Join(" or ", transition.Roles)}.");
                item.Transition = new TransitionCheck(transition, true, null, revision, Array.Empty<string>());
            }
        }
        return items;
    }

    private static IEnumerable<List<CheckInItem>> Batches(List<CheckInItem> items)
    {
        var batch = new List<CheckInItem>();
        long bytes = 0;
        foreach (var item in items)
        {
            if (batch.Count > 0 && (batch.Count >= ImportBatchFiles || bytes + item.Size > ImportBatchBytes))
            {
                yield return batch;
                batch = new List<CheckInItem>();
                bytes = 0;
            }
            batch.Add(item);
            bytes += item.Size;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Builds pointers + sidecars against the latest head and pushes, retrying if others push first.</summary>
    private async Task<string?> CommitItemsAsync(List<CheckInItem> items, CheckInOptions options, HostUser user, List<string> warnings, CancellationToken ct)
    {
        var message = BuildMessage(options, items);
        var attemptWarnings = new List<string>();
        var commit = await CommitWithRetryAsync(head =>
        {
            attemptWarnings.Clear();
            var plan = new CommitPlan();
            foreach (var item in items)
            {
                var current = head.Get(item.Path);
                if (item.IsNew && current != null)
                    throw VaultException.Conflict($"Someone else added {current.Path} to the vault while you were checking in.");
                if (!item.IsNew && (current == null || current.Oid != item.BaseOid))
                    throw VaultException.Conflict($"{item.Path} changed on the server while you were checking in.");
                item.NewVersion = item.Changed ? (current?.Version ?? 0) + 1 : current!.Version;
            }

            var batch = items.ToDictionary(i => i.Path, PathRules.Comparer);
            var now = NowIso();
            foreach (var item in items.Where(i => i.Changed || i.Transition != null))
            {
                var current = head.Get(item.Path);
                var meta = current?.Meta?.Clone() ?? new FileMeta { State = Config.Workflow.InitialState, Version = current?.Version ?? 0 };
                if (item.Changed)
                {
                    meta.Version = item.NewVersion;
                    meta.Oid = item.Oid;
                    meta.Size = item.Size;
                    meta.CheckedInBy = user.Login;
                    meta.CheckedInAt = now;
                    meta.Comment = options.Comment;
                    plan.Writes.Add((item.Path, new LfsPointer(item.Oid, item.Size).ToBytes()));
                }
                if (item.Info != null) ApplyInfo(meta, item.Info, batch, head, attemptWarnings, options.AllowMissingReferences);
                if (item.Transition != null)
                {
                    meta.State = item.Transition.Transition.To;
                    if (item.Transition.Transition.BumpRevision)
                    {
                        meta.Revision = item.Transition.NextRevision;
                        meta.RevisionHistory ??= new List<RevisionEntry>();
                        meta.RevisionHistory.Add(new RevisionEntry { Rev = meta.Revision!, Version = meta.Version, By = user.Login, At = now });
                    }
                }
                meta.State ??= Config.Workflow.InitialState;
                plan.Writes.Add((PathRules.MetaPathFor(item.Path), Encoding.UTF8.GetBytes(Json.Serialize(meta) + "\n")));
            }
            return plan;
        }, message, user, ct).ConfigureAwait(false);
        warnings.AddRange(attemptWarnings);
        return commit;
    }

    private static bool IsSolidWorksDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sldprt" or ".sldasm" or ".slddrw";

    private void ApplyInfo(FileMeta meta, CheckInFileInfo info, IReadOnlyDictionary<string, CheckInItem> batch, HeadIndex head, List<string> warnings, bool allowMissing)
    {
        if (info.References != null)
        {
            var references = new List<ReferenceEntry>();
            var external = new List<string>();
            var missing = new List<string>();
            foreach (var abs in info.References.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var vp = PathRules.ToVaultPath(LocalRoot, abs);
                if (vp == null)
                {
                    external.Add(abs);
                    // Only SOLIDWORKS files are needed to open the document. Links such as the STEP file a
                    // part was imported from (3D Interconnect) keep their geometry in the part itself.
                    if (IsSolidWorksDocument(abs) &&
                        !Config.ExternalReferenceAllowList.Any(prefix => abs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add($"{abs} is outside the vault folder; teammates won't be able to open it.");
                    continue;
                }
                var child = head.Get(vp);
                var canonical = child?.Path ?? vp;
                if (batch.TryGetValue(canonical, out var inBatch))
                {
                    references.Add(new ReferenceEntry { Path = canonical, Version = inBatch.NewVersion, Oid = inBatch.Oid });
                }
                else if (child != null)
                {
                    var record = State.GetFile(canonical);
                    var (version, oid) = record?.BaseOid != null ? (record.BaseVersion, record.BaseOid) : (child.Version, child.Oid);
                    references.Add(new ReferenceEntry { Path = canonical, Version = version, Oid = oid });
                }
                else
                {
                    missing.Add(canonical);
                }
            }
            if (missing.Count > 0 && !allowMissing)
                throw VaultException.Conflict("These referenced files are not in the vault yet; include them in the check-in: " + string.Join(", ", missing));
            meta.References = references.Count > 0 ? references.OrderBy(r => r.Path, StringComparer.Ordinal).ToList() : null;
            meta.ExternalReferences = external.Count > 0 ? external : null;
        }
        if (info.Properties != null)
        {
            var props = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in info.Properties)
                if (!string.IsNullOrEmpty(key) && value != null) props[key] = value;
            meta.Properties = props.Count > 0 ? props : null;
        }
        if (info.Configurations != null) meta.Configurations = info.Configurations.Length > 0 ? info.Configurations.ToList() : null;
        if (!string.IsNullOrEmpty(info.SwVersion)) meta.SwVersion = info.SwVersion;
    }

    private static string BuildMessage(CheckInOptions options, List<CheckInItem> items)
    {
        var sb = new StringBuilder();
        var first = string.IsNullOrWhiteSpace(options.Comment)
            ? (options.IsImport ? $"Import {items.Count} file(s)" : options.TransitionName ?? $"Check in {items.Count} file(s)")
            : options.Comment!.Trim();
        sb.Append(first.Replace("\r\n", "\n"));
        sb.Append("\n\n");
        if (options.TransitionName != null) sb.Append("Workflow: ").Append(options.TransitionName).Append('\n');
        sb.Append("Files:\n");
        foreach (var item in items.Take(50)) sb.Append("  ").Append(item.Path).Append('\n');
        if (items.Count > 50) sb.Append($"  ... and {items.Count - 50} more\n");
        return sb.ToString();
    }

    internal static GitIdentity IdentityOf(HostUser user) =>
        new(user.DisplayName, string.IsNullOrEmpty(user.Email) ? $"{user.Login}@users.noreply.swvault" : user.Email!);

    internal async Task<string?> CommitWithRetryAsync(Func<HeadIndex, CommitPlan?> buildPlan, string message, HostUser user, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            if (attempt > 1) await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            var head = _head;
            var plan = buildPlan(head);
            if (plan == null || plan.IsEmpty) return null;

            var shas = await Mirror.WriteBlobsAsync(plan.Writes.Select(w => w.Content).ToList(), ct).ConfigureAwait(false);
            var changes = plan.Writes.Select((w, i) => new TreeChange(w.Path, shas[i]))
                .Concat(plan.Deletes.Select(d => new TreeChange(d, null)))
                .ToList();
            var commit = await Mirror.CreateCommitAsync(head.Commit, changes, message, IdentityOf(user), ct).ConfigureAwait(false);
            if (await Mirror.PushAsync(commit, ct).ConfigureAwait(false) == PushOutcome.Pushed)
            {
                await Mirror.UpdateRemoteRefAsync(commit, ct).ConfigureAwait(false);
                await RebuildIndexAsync(ct).ConfigureAwait(false);
                return commit;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500) * attempt), ct).ConfigureAwait(false);
        }
        throw VaultException.Conflict("Other check-ins kept landing first. Please try again.");
    }

    /// <summary>Releases the lock and makes the local file read-only again.</summary>
    internal async Task ReleaseAsync(string vaultPath, CancellationToken ct)
    {
        var lockRecord = LockOf(vaultPath);
        if (lockRecord is { Mine: true })
        {
            await Locks.UnlockAsync(lockRecord.Id, force: false, ct).ConfigureAwait(false);
            ClearLock(vaultPath);
        }
        MarkLockedHere(vaultPath, false);
        var local = LocalPathOf(vaultPath);
        if (File.Exists(local)) SetReadOnlyAttribute(local, true);
    }

    /// <summary>Finishes unlocks recorded before a crash (pushed, but locks not yet released).</summary>
    public async Task ResumePendingAsync(CancellationToken ct = default)
    {
        foreach (var job in State.GetJobs().Where(j => j.Kind == "unlock"))
        {
            var paths = Json.Deserialize<List<string>>(job.Data) ?? new List<string>();
            await SyncAsync(fetch: false, refreshLocks: true, ct).ConfigureAwait(false);
            foreach (var path in paths)
            {
                // Only release files whose checked-in content is what's on disk; anything else stays checked out.
                var head = _head.Get(path);
                var (state, _, _) = await ComputeLocalStateAsync(path, head, ct).ConfigureAwait(false);
                if (head != null && state == LocalState.UpToDate) await ReleaseAsync(path, ct).ConfigureAwait(false);
            }
            State.RemoveJob(job.Id);
        }
    }
}
