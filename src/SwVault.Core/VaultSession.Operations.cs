using SwVault.Core.Index;
using SwVault.Core.Lfs;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed partial class VaultSession
{
    /// <summary>Resolves requested paths to head files: folders expand to their contents, files optionally to their references.</summary>
    internal IReadOnlyList<HeadFile> ExpandTargets(IEnumerable<string> paths, bool withReferences, List<string> warnings)
    {
        var result = new Dictionary<string, HeadFile>(PathRules.Comparer);
        var queue = new Queue<HeadFile>();
        foreach (var raw in paths)
        {
            var vaultPath = ResolveVaultPath(raw);
            if (vaultPath == null)
            {
                warnings.Add($"{raw} is outside the vault folder.");
                continue;
            }
            var file = _head.Get(vaultPath);
            if (file != null)
            {
                queue.Enqueue(file);
                continue;
            }
            var prefix = vaultPath.Length == 0 ? "" : vaultPath + "/";
            var inFolder = _head.Files.Values.Where(f => f.Path.StartsWith(prefix, PathRules.Comparison)).ToList();
            if (inFolder.Count == 0 && !File.Exists(LocalPathOf(vaultPath))) warnings.Add($"{vaultPath} is not in the vault.");
            foreach (var f in inFolder) queue.Enqueue(f);
        }

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!result.TryAdd(file.Path, file) || !withReferences) continue;
            foreach (var reference in file.Meta?.References ?? new List<ReferenceEntry>())
            {
                var child = _head.Get(reference.Path);
                if (child != null && !result.ContainsKey(child.Path)) queue.Enqueue(child);
            }
        }
        return result.Values.ToList();
    }

    public async Task<StagedOperation> PrepareGetLatestAsync(IReadOnlyList<string> paths, bool withReferences, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await SyncAsync(ct: ct).ConfigureAwait(false);
        var op = new StagedOperation { Kind = JobKind.GetLatest };
        foreach (var head in ExpandTargets(paths, withReferences, op.Warnings))
        {
            var (state, record, _) = await ComputeLocalStateAsync(head.Path, head, ct).ConfigureAwait(false);
            var lockState = LockStateOf(head.Path, record, out _);
            var readOnly = lockState != LockState.MineHere;
            switch (state)
            {
                case LocalState.NotLocal:
                case LocalState.MissingLocally:
                case LocalState.Outdated:
                    op.Changes.Add(WriteChange(head, readOnly));
                    break;
                case LocalState.UpToDate:
                    if (readOnly && !File.GetAttributes(LocalPathOf(head.Path)).HasFlag(FileAttributes.ReadOnly))
                        op.Changes.Add(new StagedChange { VaultPath = head.Path, LocalPath = LocalPathOf(head.Path), Action = StagedAction.SetReadOnly });
                    break;
                case LocalState.Modified:
                case LocalState.Conflict:
                    op.Warnings.Add($"{head.Path} has local changes and was not replaced. Check it in, or use Undo Check Out to discard them.");
                    break;
            }
        }

        // Files deleted on the server: remove unmodified local copies inside the requested folders.
        foreach (var raw in paths)
        {
            var vaultPath = ResolveVaultPath(raw);
            if (vaultPath == null || _head.Get(vaultPath) != null) continue;
            foreach (var record in State.GetAllFiles().Where(r => r.BaseOid != null && _head.Get(r.Path) == null
                && (vaultPath.Length == 0 || r.Path.Equals(vaultPath, PathRules.Comparison) || r.Path.StartsWith(vaultPath + "/", PathRules.Comparison))))
            {
                var local = LocalPathOf(record.Path);
                if (!File.Exists(local)) continue;
                var current = await CurrentOidAsync(record.Path, new FileInfo(local), record, ct).ConfigureAwait(false);
                if (current == record.BaseOid)
                    op.Changes.Add(new StagedChange { VaultPath = record.Path, LocalPath = local, Action = StagedAction.Delete });
                else
                    op.Warnings.Add($"{record.Path} was deleted from the vault but has local changes; kept.");
            }
        }

        await StageDownloadsAsync(op, progress, ct).ConfigureAwait(false);
        return op;
    }

    /// <summary>
    /// Locks the files on the server, then brings each local copy to the head version and makes it
    /// writable. Locking first means nobody can check in a newer version after we look.
    /// </summary>
    /// <param name="forTransition">
    /// Workflow transition the check-out is for (e.g. an approver stamping the revision into an
    /// InReview drawing before "Approve"). Allows read-only states when the user may run that transition.
    /// </param>
    public async Task<StagedOperation> CheckOutAsync(IReadOnlyList<string> paths, bool takeOver = false, string? forTransition = null,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = await RequireUserAsync(ct).ConfigureAwait(false);
            await SyncAsync(ct: ct).ConfigureAwait(false);
            var op = new StagedOperation { Kind = JobKind.CheckOut };
            var failures = new List<string>();
            var locked = new List<string>();

            foreach (var raw in paths.Distinct(PathRules.Comparer))
            {
                var vaultPath = ResolveVaultPath(raw);
                var head = vaultPath == null ? null : _head.Get(vaultPath);
                if (head == null)
                {
                    if (vaultPath != null && File.Exists(LocalPathOf(vaultPath)))
                        op.Warnings.Add($"{vaultPath} is a new file that isn't in the vault yet; it is already editable.");
                    else
                        failures.Add($"{raw} is not in the vault.");
                    continue;
                }

                var state = WorkflowEngine.CurrentState(head.Meta, Config);
                if (forTransition != null)
                {
                    var check = WorkflowEngine.Evaluate(Config, head, forTransition, user.Login, _head);
                    if (!check.Allowed)
                    {
                        failures.Add($"{head.Path}: {check.Reason}");
                        continue;
                    }
                }
                else if (Config.IsReadOnlyState(state))
                {
                    failures.Add($"{head.Path} is {state}. Use the 'Change request' workflow action before checking it out.");
                    continue;
                }

                var record = State.GetFile(head.Path);
                switch (LockStateOf(head.Path, record, out var existing))
                {
                    case LockState.MineHere:
                        locked.Add(head.Path);
                        continue;
                    case LockState.Other:
                        failures.Add($"{head.Path} is checked out by {existing!.Owner}.");
                        continue;
                    case LockState.MineElsewhere when !takeOver:
                        failures.Add($"{head.Path} is checked out by you on another computer. Check it in there, or take over the check-out here.");
                        continue;
                    case LockState.MineElsewhere:
                        MarkLockedHere(head.Path, true);
                        locked.Add(head.Path);
                        continue;
                }

                var created = await Locks.CreateAsync(head.Path, ct).ConfigureAwait(false);
                if (!created.Success)
                {
                    failures.Add($"{head.Path} is checked out by {created.Existing?.OwnerName ?? "someone else"}.");
                    continue;
                }
                SetLock(new LockRecord(created.Created!.Id, head.Path, created.Created.OwnerName.Length > 0 ? created.Created.OwnerName : user.Login, created.Created.LockedAt, true));
                MarkLockedHere(head.Path, true);
                locked.Add(head.Path);
            }

            if (locked.Count == 0 && failures.Count > 0) throw VaultException.Conflict(string.Join("\n", failures));

            // Someone may have checked in between our first look and taking the lock.
            await SyncAsync(fetch: true, refreshLocks: false, ct).ConfigureAwait(false);
            foreach (var path in locked)
            {
                var head = _head.Get(path)!;
                var (state, _, _) = await ComputeLocalStateAsync(path, head, ct).ConfigureAwait(false);
                switch (state)
                {
                    case LocalState.NotLocal:
                    case LocalState.MissingLocally:
                    case LocalState.Outdated:
                        op.Changes.Add(WriteChange(head, readOnly: false));
                        break;
                    case LocalState.Conflict:
                        op.Warnings.Add($"{path}: your local copy has changes made to an older version. It was kept; use Undo Check Out to get the latest instead.");
                        op.Changes.Add(new StagedChange { VaultPath = path, LocalPath = LocalPathOf(path), Action = StagedAction.SetWritable });
                        break;
                    default:
                        op.Changes.Add(new StagedChange { VaultPath = path, LocalPath = LocalPathOf(path), Action = StagedAction.SetWritable });
                        break;
                }
            }

            op.Warnings.AddRange(failures);
            await StageDownloadsAsync(op, progress, ct).ConfigureAwait(false);
            return op;
        }
        finally
        {
            _opGate.Release();
        }
    }

    internal void MarkLockedHere(string vaultPath, bool lockedHere)
    {
        var record = State.GetFile(vaultPath) ?? new FileRecord { Path = vaultPath };
        record.LockedHere = lockedHere;
        State.UpsertFile(record);
    }

    /// <summary>Restores the server version and releases the lock once the files are replaced.</summary>
    public async Task<StagedOperation> PrepareUndoCheckOutAsync(IReadOnlyList<string> paths, bool discardChanges, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await SyncAsync(ct: ct).ConfigureAwait(false);
        var op = new StagedOperation { Kind = JobKind.UndoCheckOut };
        var toRelease = new List<LockRecord>();
        var releaseAlways = new List<LockRecord>();

        foreach (var raw in paths.Distinct(PathRules.Comparer))
        {
            var vaultPath = ResolveVaultPath(raw);
            if (vaultPath == null) continue;
            var lockRecord = LockOf(vaultPath);
            if (lockRecord is not { Mine: true })
            {
                op.Warnings.Add($"{vaultPath} is not checked out by you.");
                continue;
            }
            var head = _head.Get(vaultPath);
            if (head == null)
            {
                releaseAlways.Add(lockRecord); // a new file that was locked but never checked in
                continue;
            }

            var (state, _, _) = await ComputeLocalStateAsync(head.Path, head, ct).ConfigureAwait(false);
            if (state is LocalState.Modified or LocalState.Conflict && !discardChanges)
                throw VaultException.Conflict($"{head.Path} has changes that would be lost. Confirm discarding them to undo the check-out.");
            op.Changes.Add(state == LocalState.UpToDate
                ? new StagedChange { VaultPath = head.Path, LocalPath = LocalPathOf(head.Path), Action = StagedAction.SetReadOnly }
                : WriteChange(head, readOnly: true));
            toRelease.Add(lockRecord);
        }

        op.AfterApply.Add(async (applied, token) =>
        {
            var appliedSet = new HashSet<string>(applied, PathRules.Comparer);
            foreach (var l in toRelease.Where(l => appliedSet.Contains(l.Path)).Concat(releaseAlways))
            {
                await Locks.UnlockAsync(l.Id, force: false, token).ConfigureAwait(false);
                ClearLock(l.Path);
                MarkLockedHere(l.Path, false);
            }
        });

        await StageDownloadsAsync(op, progress, ct).ConfigureAwait(false);
        return op;
    }

    /// <summary>Puts an older version in the workspace, read-only; "as built" also restores the child versions it was checked in with.</summary>
    public async Task<StagedOperation> PrepareGetVersionAsync(string path, int version, bool asBuilt, bool discardChanges = false, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await SyncAsync(ct: ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        var target = await FindVersionAsync(vaultPath, version, ct).ConfigureAwait(false);
        var op = new StagedOperation { Kind = JobKind.GetVersion };

        await AddVersionWriteAsync(op, vaultPath, target.Meta.Oid, target.Meta.Size, target.Meta.Version, discardChanges, ct).ConfigureAwait(false);
        if (asBuilt)
        {
            foreach (var reference in target.Meta.References ?? new List<ReferenceEntry>())
            {
                if (string.IsNullOrEmpty(reference.Oid)) continue;
                var size = await SizeOfVersionAsync(reference.Path, reference.Version, reference.Oid, ct).ConfigureAwait(false);
                if (size < 0)
                {
                    op.Warnings.Add($"{reference.Path} v{reference.Version} could not be found in the history.");
                    continue;
                }
                await AddVersionWriteAsync(op, reference.Path, reference.Oid, size, reference.Version, discardChanges, ct).ConfigureAwait(false);
            }
        }
        await StageDownloadsAsync(op, progress, ct).ConfigureAwait(false);
        return op;
    }

    private async Task AddVersionWriteAsync(StagedOperation op, string vaultPath, string oid, long size, int version, bool discardChanges, CancellationToken ct)
    {
        var head = _head.Get(vaultPath);
        var canonical = head?.Path ?? vaultPath;
        var record = State.GetFile(canonical);
        if (LockStateOf(canonical, record, out _) is LockState.MineHere or LockState.MineElsewhere)
        {
            op.Warnings.Add($"{canonical} is checked out by you; use Rollback to bring back an older version.");
            return;
        }
        var (state, _, _) = await ComputeLocalStateAsync(canonical, head, ct).ConfigureAwait(false);
        if (state is LocalState.Modified or LocalState.Conflict && !discardChanges)
        {
            op.Warnings.Add($"{canonical} has local changes and was not replaced.");
            return;
        }
        if (record?.BaseOid == oid && state is LocalState.UpToDate or LocalState.Outdated) return; // already that content
        op.Changes.Add(new StagedChange
        {
            VaultPath = canonical,
            LocalPath = LocalPathOf(canonical),
            Action = StagedAction.Write,
            Oid = oid,
            Size = size,
            Version = version,
            ReadOnly = true,
        });
    }

    /// <summary>Checked-out file only: puts an older version's content in place as a pending change.</summary>
    public async Task<StagedOperation> PrepareRollbackAsync(string path, int version, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await SyncAsync(ct: ct).ConfigureAwait(false);
        var vaultPath = ResolveVaultPath(path) ?? throw VaultException.BadRequest($"{path} is outside the vault folder.");
        var head = _head.Get(vaultPath) ?? throw VaultException.NotFound($"{vaultPath} is not in the vault.");
        if (LockStateOf(head.Path, State.GetFile(head.Path), out _) != LockState.MineHere)
            throw VaultException.Conflict($"Check out {head.Path} before rolling it back.");
        var target = await FindVersionAsync(head.Path, version, ct).ConfigureAwait(false);
        var op = new StagedOperation { Kind = JobKind.Rollback };
        op.Changes.Add(new StagedChange
        {
            VaultPath = head.Path,
            LocalPath = LocalPathOf(head.Path),
            Action = StagedAction.Write,
            Oid = target.Meta.Oid,
            Size = target.Meta.Size,
            Version = target.Meta.Version,
            ReadOnly = false,
            UpdateBase = false,
        });
        await StageDownloadsAsync(op, progress, ct).ConfigureAwait(false);
        return op;
    }
}
