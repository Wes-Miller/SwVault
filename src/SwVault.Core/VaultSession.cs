using System.Globalization;
using SwVault.Core.Auth;
using SwVault.Core.Git;
using SwVault.Core.Hosts;
using SwVault.Core.Index;
using SwVault.Core.Lfs;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core;

public sealed record VaultSessionOptions
{
    public required string VaultId { get; init; }

    /// <summary>Git URL of the vault repository (or a local bare repo path in tests).</summary>
    public required string RemoteUrl { get; init; }
    public required string LocalRoot { get; init; }

    /// <summary>Per-vault data folder on this PC (mirror.git, state.db).</summary>
    public required string DataDir { get; init; }
    public required ICredentialProvider Credentials { get; init; }
    public IHostAdapter? Host { get; init; }
    public Uri? LfsUrl { get; init; }
    public HttpMessageHandler? HttpHandler { get; init; }
    public bool AllowSyncedFolderRoot { get; init; }
}

public sealed record SyncResult(bool HeadChanged, IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> LockChangedPaths);

/// <summary>
/// One vault on this PC: the pointer-only mirror, local state, and every PDM operation.
/// Thread-safe for concurrent reads; mutating operations are serialized.
/// </summary>
public sealed partial class VaultSession : IDisposable
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly HttpClient _http;
    private Dictionary<string, LockRecord> _locks;
    private IgnoreRules _ignore;
    private HeadIndex _head;

    public string VaultId { get; }
    public string RemoteUrl { get; }
    public string LocalRoot { get; }
    public string DataDir { get; }
    public HostUser? User { get; private set; }
    public HeadIndex Head => _head;
    public VaultConfig Config => _head.Config;
    public bool Online { get; private set; }
    public DateTimeOffset? LastSyncUtc { get; private set; }
    public string? LastError { get; private set; }

    internal StateStore State { get; }
    internal GitMirror Mirror { get; }
    internal LfsClient Lfs { get; }
    internal LockClient Locks { get; }
    internal AuthContext Auth { get; }
    internal IHostAdapter Host { get; }

    private VaultSession(VaultSessionOptions options, StateStore state, GitMirror mirror, HttpClient http, AuthContext auth,
        LfsClient lfs, LockClient locks, IHostAdapter host, HeadIndex head)
    {
        VaultId = options.VaultId;
        RemoteUrl = options.RemoteUrl;
        LocalRoot = Path.GetFullPath(options.LocalRoot);
        DataDir = options.DataDir;
        State = state;
        Mirror = mirror;
        _http = http;
        Auth = auth;
        Lfs = lfs;
        Locks = locks;
        Host = host;
        _head = head;
        _ignore = new IgnoreRules(head.Config.Ignore);
        _locks = state.GetLocks().ToDictionary(l => l.Path, PathRules.Comparer);
    }

    public static async Task<VaultSession> OpenAsync(VaultSessionOptions options, CancellationToken ct = default)
    {
        if (!options.AllowSyncedFolderRoot && PathRules.IsUnderSyncedFolder(options.LocalRoot))
            throw VaultException.BadRequest($"The vault folder {options.LocalRoot} is inside OneDrive/Dropbox/Google Drive. Cloud sync fights with SOLIDWORKS and SwVault over file locks; pick a folder like C:\\SWVault instead.");

        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(options.LocalRoot);
        var server = ServerUriFor(options.RemoteUrl);
        var auth = new AuthContext(server, options.Credentials);
        var http = options.HttpHandler == null ? new HttpClient() : new HttpClient(options.HttpHandler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var state = new StateStore(Path.Combine(options.DataDir, "state.db"));
        GitMirror? mirror = null;
        try
        {
            mirror = await GitMirror.OpenOrCreateAsync(Path.Combine(options.DataDir, "mirror.git"), options.RemoteUrl, auth.GitEnvironmentAsync, ct).ConfigureAwait(false);
            var lfsUrl = options.LfsUrl ?? HostAdapters.LfsUrlFor(options.RemoteUrl);
            var lfs = new LfsClient(http, lfsUrl, auth);
            var locks = new LockClient(http, lfsUrl, auth);
            var host = options.Host ?? HostAdapters.Create(server, http, auth);
            var fallback = VaultConfig.CreateDefault(Path.GetFileName(options.LocalRoot.TrimEnd('\\')), options.LocalRoot, "", null);
            var head = await HeadIndex.BuildAsync(mirror, state, await mirror.GetHeadAsync(ct).ConfigureAwait(false), fallback, ct).ConfigureAwait(false);
            var session = new VaultSession(options, state, mirror, http, auth, lfs, locks, host, head);
            var cachedUser = state.GetValue("user");
            if (cachedUser != null) session.User = Json.Deserialize<HostUser>(cachedUser);
            return session;
        }
        catch
        {
            mirror?.Dispose();
            state.Dispose();
            http.Dispose();
            throw;
        }
    }

    private static Uri ServerUriFor(string remoteUrl) =>
        Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(remoteUrl));

    // ---------------------------------------------------------------- identity & sync

    public async Task<HostUser> SignInAsync(bool interactive, CancellationToken ct = default)
    {
        var credential = await Auth.GetCredentialAsync(interactive, ct).ConfigureAwait(false);
        if (credential == null && Host is not StaticHostAdapter)
            throw VaultException.Unauthorized($"Not signed in to {Auth.Server.Authority}. Sign in from the SwVault settings (or run 'swvault login').");
        var user = await Host.GetCurrentUserAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(user.Login)) throw VaultException.Unauthorized("The server did not return a user name for this sign-in.");
        User = user;
        State.SetValue("user", Json.Serialize(user));
        return user;
    }

    internal async Task<HostUser> RequireUserAsync(CancellationToken ct) => User ?? await SignInAsync(false, ct).ConfigureAwait(false);

    /// <summary>Fetches the server head (only if it moved) and refreshes the lock list.</summary>
    public async Task<SyncResult> SyncAsync(bool fetch = true, bool refreshLocks = true, CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldHead = _head;
            var oldLocks = _locks;
            try
            {
                if (fetch)
                {
                    var remoteHead = await Mirror.LsRemoteHeadAsync(ct).ConfigureAwait(false);
                    var localHead = await Mirror.GetHeadAsync(ct).ConfigureAwait(false);
                    if (remoteHead != null && remoteHead != localHead)
                    {
                        await Mirror.FetchAsync(ct).ConfigureAwait(false);
                        await RebuildIndexAsync(ct).ConfigureAwait(false);
                    }
                }
                if (refreshLocks) await RefreshLocksAsync(ct).ConfigureAwait(false);
                Online = true;
                LastError = null;
                LastSyncUtc = DateTimeOffset.UtcNow;
            }
            catch (VaultException ex) when (ex.Code is ErrorCodes.Offline or ErrorCodes.Unauthorized)
            {
                Online = false;
                LastError = ex.Message;
                throw;
            }

            var changed = oldHead.Files.Values.Select(f => f.Path).Union(_head.Files.Values.Select(f => f.Path), PathRules.Comparer)
                .Where(p => oldHead.Get(p)?.Oid != _head.Get(p)?.Oid || oldHead.Get(p)?.Meta?.State != _head.Get(p)?.Meta?.State)
                .ToList();
            var lockChanged = oldLocks.Keys.Union(_locks.Keys, PathRules.Comparer)
                .Where(p => !oldLocks.TryGetValue(p, out var a) || !_locks.TryGetValue(p, out var b) || a.Id != b.Id)
                .ToList();
            return new SyncResult(oldHead.Commit != _head.Commit, changed, lockChanged);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Sync for read-only views: offline or signed-out just means showing the last known data.</summary>
    public async Task<bool> TrySyncAsync(bool refreshLocks = true, CancellationToken ct = default)
    {
        try
        {
            await SyncAsync(fetch: true, refreshLocks: refreshLocks, ct).ConfigureAwait(false);
            return true;
        }
        catch (VaultException ex) when (ex.Code is ErrorCodes.Offline or ErrorCodes.Unauthorized)
        {
            return false;
        }
    }

    internal async Task RebuildIndexAsync(CancellationToken ct)
    {
        var commit = await Mirror.GetHeadAsync(ct).ConfigureAwait(false);
        _head = await HeadIndex.BuildAsync(Mirror, State, commit, _head.Config, ct).ConfigureAwait(false);
        _ignore = new IgnoreRules(_head.Config.Ignore);
    }

    private async Task RefreshLocksAsync(CancellationToken ct)
    {
        var user = await RequireUserAsync(ct).ConfigureAwait(false);
        List<LockRecord> records;
        var verified = await Locks.VerifyAsync(ct).ConfigureAwait(false);
        if (verified is { } v)
        {
            records = v.Ours.Select(l => ToRecord(l, true)).Concat(v.Theirs.Select(l => ToRecord(l, false))).ToList();
        }
        else
        {
            var all = await Locks.ListAllAsync(ct).ConfigureAwait(false);
            records = all.Select(l => ToRecord(l, string.Equals(l.OwnerName, user.Login, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        State.ReplaceLocks(records);
        _locks = records.GroupBy(r => r.Path, PathRules.Comparer).ToDictionary(g => g.Key, g => g.First(), PathRules.Comparer);
    }

    private static LockRecord ToRecord(LfsLock l, bool mine) => new(l.Id, PathRules.Normalize(l.Path), l.OwnerName, l.LockedAt, mine);

    // ---------------------------------------------------------------- paths

    public string LocalPathOf(string vaultPath) => PathRules.ToLocalPath(LocalRoot, vaultPath);

    /// <summary>Accepts an absolute local path or a vault path; returns the vault path (server case when known).</summary>
    public string? ResolveVaultPath(string pathOrLocal)
    {
        string? vaultPath;
        if (Path.IsPathFullyQualified(pathOrLocal))
        {
            if (string.Equals(Path.GetFullPath(pathOrLocal).TrimEnd('\\'), LocalRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return ""; // the vault root folder itself
            vaultPath = PathRules.ToVaultPath(LocalRoot, pathOrLocal);
            if (vaultPath == null) return null;
        }
        else
        {
            vaultPath = PathRules.Normalize(pathOrLocal);
        }
        return _head.Get(vaultPath)?.Path ?? vaultPath;
    }

    public bool IsIgnored(string vaultPath) => PathRules.IsInternal(vaultPath) || _ignore.IsIgnored(vaultPath);

    internal LockRecord? LockOf(string vaultPath) => _locks.TryGetValue(PathRules.Normalize(vaultPath), out var l) ? l : null;

    internal void SetLock(LockRecord record)
    {
        State.PutLock(record);
        _locks = new Dictionary<string, LockRecord>(_locks, PathRules.Comparer) { [record.Path] = record };
    }

    internal void ClearLock(string vaultPath)
    {
        State.RemoveLock(vaultPath);
        var copy = new Dictionary<string, LockRecord>(_locks, PathRules.Comparer);
        copy.Remove(vaultPath);
        _locks = copy;
    }

    // ---------------------------------------------------------------- status

    internal async Task<(LocalState State, FileRecord? Record, string? CurrentOid)> ComputeLocalStateAsync(string vaultPath, HeadFile? head, CancellationToken ct)
    {
        if (IsIgnored(vaultPath)) return (LocalState.Ignored, null, null);
        var file = new FileInfo(LocalPathOf(vaultPath));
        var record = State.GetFile(vaultPath);

        if (head == null)
        {
            if (!file.Exists) return (LocalState.NotLocal, record, null);
            return (record?.BaseOid != null ? LocalState.DeletedOnServer : LocalState.LocalOnly, record, null);
        }
        if (!file.Exists) return (record?.SnapSize != null ? LocalState.MissingLocally : LocalState.NotLocal, record, null);

        var current = await CurrentOidAsync(vaultPath, file, record, ct).ConfigureAwait(false);
        record = State.GetFile(vaultPath);
        if (record?.BaseOid == null)
        {
            if (current != head.Oid) return (LocalState.Conflict, record, current);
            // A matching file that SwVault didn't write (copied in, or state was reset): adopt it.
            record ??= new FileRecord { Path = head.Path };
            record.BaseOid = head.Oid;
            record.BaseSize = head.Size;
            record.BaseVersion = head.Version;
            record.BaseCommit = _head.Commit;
            record.SnapSize = file.Length;
            record.SnapMtime = file.LastWriteTimeUtc.Ticks;
            State.UpsertFile(record);
            return (LocalState.UpToDate, record, current);
        }

        var modified = current != record.BaseOid;
        var outdated = record.BaseOid != head.Oid;
        var state = modified && outdated ? LocalState.Conflict
            : modified ? LocalState.Modified
            : outdated ? LocalState.Outdated
            : LocalState.UpToDate;
        return (state, record, current);
    }

    /// <summary>Content oid of the local file, hashing only when size/mtime changed since we last looked.</summary>
    internal async Task<string> CurrentOidAsync(string vaultPath, FileInfo file, FileRecord? record, CancellationToken ct)
    {
        var size = file.Length;
        var mtime = file.LastWriteTimeUtc.Ticks;
        if (record?.BaseOid != null && record.SnapSize == size && record.SnapMtime == mtime) return record.BaseOid;
        if (record?.HashOid != null && record.HashSize == size && record.HashMtime == mtime) return record.HashOid;
        var (oid, _) = await Hashing.Sha256FileAsync(file.FullName, ct).ConfigureAwait(false);
        record ??= new FileRecord { Path = vaultPath };
        record.HashOid = oid;
        record.HashSize = size;
        record.HashMtime = mtime;
        State.UpsertFile(record);
        return oid;
    }

    internal LockState LockStateOf(string vaultPath, FileRecord? record, out LockRecord? lockRecord)
    {
        lockRecord = LockOf(vaultPath);
        if (lockRecord == null) return LockState.None;
        if (!lockRecord.Mine) return LockState.Other;
        return record?.LockedHere == true ? LockState.MineHere : LockState.MineElsewhere;
    }

    public async Task<FileStatusDto?> GetStatusAsync(string pathOrLocal, CancellationToken ct = default)
    {
        var vaultPath = ResolveVaultPath(pathOrLocal);
        if (vaultPath == null) return null;
        if (Directory.Exists(LocalPathOf(vaultPath)) || _head.Folders.Contains(vaultPath))
            return new FileStatusDto { VaultId = VaultId, Path = vaultPath, LocalPath = LocalPathOf(vaultPath), IsFolder = true };
        var head = _head.Get(vaultPath);
        var (state, record, _) = await ComputeLocalStateAsync(vaultPath, head, ct).ConfigureAwait(false);
        if (head == null && state == LocalState.NotLocal) return null;
        return BuildStatus(head?.Path ?? vaultPath, head, state, record);
    }

    internal FileStatusDto BuildStatus(string vaultPath, HeadFile? head, LocalState state, FileRecord? record)
    {
        var lockState = LockStateOf(vaultPath, record, out var lockRecord);
        var meta = head?.Meta;
        var local = LocalPathOf(vaultPath);
        return new FileStatusDto
        {
            VaultId = VaultId,
            Path = vaultPath,
            LocalPath = local,
            LocalState = state,
            LockState = lockState,
            LockOwner = lockRecord?.Owner,
            LockedAt = lockRecord?.LockedAt,
            LocalVersion = record?.BaseOid != null ? record.BaseVersion : 0,
            ServerVersion = head?.Version ?? 0,
            State = head == null ? null : WorkflowEngine.CurrentState(meta, Config),
            Revision = meta?.Revision,
            Size = head?.Size ?? (File.Exists(local) ? new FileInfo(local).Length : 0),
            CheckedInBy = meta?.CheckedInBy,
            CheckedInAt = meta?.CheckedInAt,
            Comment = meta?.Comment,
            Properties = meta?.Properties == null ? null : new Dictionary<string, string>(meta.Properties),
        };
    }

    /// <summary>Direct children of a vault folder: server files, local-only files, and subfolders.</summary>
    public async Task<IReadOnlyList<FileStatusDto>> ListFolderAsync(string folder, CancellationToken ct = default)
    {
        folder = PathRules.Normalize(folder);
        var result = new List<FileStatusDto>();
        var seen = new HashSet<string>(PathRules.Comparer);

        foreach (var sub in _head.Folders.Where(f => string.Equals(PathRules.GetFolder(f), folder, PathRules.Comparison)))
        {
            if (IsIgnored(sub + "/x") || !seen.Add(sub)) continue;
            result.Add(new FileStatusDto { VaultId = VaultId, Path = sub, LocalPath = LocalPathOf(sub), IsFolder = true });
        }
        var localFolder = folder.Length == 0 ? LocalRoot : LocalPathOf(folder);
        if (Directory.Exists(localFolder))
        {
            foreach (var dir in Directory.EnumerateDirectories(localFolder))
            {
                var vp = PathRules.Combine(folder, Path.GetFileName(dir));
                if (PathRules.IsInternal(vp) || IsIgnored(vp + "/x") || !seen.Add(vp)) continue;
                result.Add(new FileStatusDto { VaultId = VaultId, Path = vp, LocalPath = dir, IsFolder = true });
            }
        }

        foreach (var head in _head.Files.Values.Where(f => string.Equals(PathRules.GetFolder(f.Path), folder, PathRules.Comparison)))
        {
            seen.Add(head.Path);
            var (state, record, _) = await ComputeLocalStateAsync(head.Path, head, ct).ConfigureAwait(false);
            result.Add(BuildStatus(head.Path, head, state, record));
        }
        if (Directory.Exists(localFolder))
        {
            foreach (var file in Directory.EnumerateFiles(localFolder))
            {
                var vp = PathRules.Combine(folder, Path.GetFileName(file));
                if (!seen.Add(vp) || IsIgnored(vp)) continue;
                var (state, record, _) = await ComputeLocalStateAsync(vp, null, ct).ConfigureAwait(false);
                result.Add(BuildStatus(vp, null, state, record));
            }
        }
        return result.OrderByDescending(s => s.IsFolder).ThenBy(s => s.Path, PathRules.Comparer).ToList();
    }

    /// <summary>Workspace files that are not on the server yet.</summary>
    public IEnumerable<string> EnumerateLocalOnly(string folder = "")
    {
        var root = folder.Length == 0 ? LocalRoot : LocalPathOf(folder);
        if (!Directory.Exists(root)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var vp = PathRules.ToVaultPath(LocalRoot, file);
            if (vp == null || IsIgnored(vp) || _head.Get(vp) != null) continue;
            yield return vp;
        }
    }

    public async Task<IReadOnlyList<FileStatusDto>> SearchAsync(string? query, StatusFilter filter, int max, CancellationToken ct = default)
    {
        if (max <= 0) max = 500;
        var q = query?.Trim();
        bool Matches(string path, HeadFile? head) =>
            string.IsNullOrEmpty(q)
            || PathRules.GetFileName(path).Contains(q, StringComparison.OrdinalIgnoreCase)
            || path.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (head?.Meta?.Properties?.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false);

        IEnumerable<string> candidates = filter switch
        {
            StatusFilter.MyCheckouts => _locks.Values.Where(l => l.Mine).Select(l => l.Path),
            StatusFilter.CheckedOut => _locks.Values.Select(l => l.Path),
            StatusFilter.LocalOnly => EnumerateLocalOnly(),
            StatusFilter.InReview => _head.Files.Values.Where(f => string.Equals(f.Meta?.State, "InReview", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path),
            _ => _head.Files.Values.Select(f => f.Path),
        };

        var result = new List<FileStatusDto>();
        foreach (var path in candidates.Distinct(PathRules.Comparer))
        {
            if (result.Count >= max) break;
            var head = _head.Get(path);
            if (!Matches(path, head)) continue;
            var (state, record, _) = await ComputeLocalStateAsync(path, head, ct).ConfigureAwait(false);
            if (filter == StatusFilter.Outdated && state != LocalState.Outdated) continue;
            if (filter == StatusFilter.Modified && state is not (LocalState.Modified or LocalState.Conflict or LocalState.LocalOnly)) continue;
            if (head == null && state == LocalState.NotLocal) continue;
            result.Add(BuildStatus(head?.Path ?? path, head, state, record));
        }
        return result;
    }

    public IReadOnlyList<LockDto> GetLocks() =>
        _locks.Values.OrderBy(l => l.Path, PathRules.Comparer)
            .Select(l => new LockDto { Id = l.Id, Path = l.Path, Owner = l.Owner, LockedAt = l.LockedAt, Mine = l.Mine })
            .ToList();

    internal static string NowIso() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Mirror.Dispose();
        State.Dispose();
        _http.Dispose();
        _syncGate.Dispose();
        _opGate.Dispose();
    }
}
