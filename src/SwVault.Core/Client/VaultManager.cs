using System.Security.Cryptography;
using System.Text;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Core.Util;

namespace SwVault.Core.Client;

/// <summary>A vault this PC is connected to.</summary>
public sealed class VaultRegistration
{
    public string Id { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string LocalRoot { get; set; } = "";
    public string? Name { get; set; }
}

/// <summary>
/// Per-user SwVault data: %LOCALAPPDATA%\SwVault (or ...\SwVault\profiles\&lt;name&gt; for extra
/// profiles, used to simulate a second user on one PC during development).
/// </summary>
public sealed class SwVaultProfile
{
    public string Name { get; }
    public string BaseDir { get; }

    public SwVaultProfile(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Environment.GetEnvironmentVariable("SWVAULT_PROFILE") ?? "default" : name!;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwVault");
        BaseDir = Name == "default" ? root : Path.Combine(root, "profiles", Name);
        Directory.CreateDirectory(BaseDir);
    }

    public string RegistryPath => Path.Combine(BaseDir, "vaults.json");
    public string LogDir => Path.Combine(BaseDir, "logs");
    public string VaultDataDir(string vaultId) => Path.Combine(BaseDir, "vaults", vaultId);

    /// <summary>Named pipe the agent listens on; includes the user SID so it's per user.</summary>
    public string PipeName
    {
        get
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var name = Protocol.ProtocolInfo.PipeName(sid);
            return Name == "default" ? name : name + "." + Name;
        }
    }

    public ICredentialProvider CredentialProvider => CredentialChain.Default(Name);

    public List<VaultRegistration> LoadRegistrations()
    {
        if (!File.Exists(RegistryPath)) return new List<VaultRegistration>();
        return Json.Deserialize<List<VaultRegistration>>(File.ReadAllText(RegistryPath)) ?? new List<VaultRegistration>();
    }

    public void SaveRegistrations(List<VaultRegistration> registrations)
    {
        var temp = RegistryPath + ".tmp";
        File.WriteAllText(temp, Json.Serialize(registrations));
        File.Move(temp, RegistryPath, overwrite: true);
    }

    /// <summary>Stable id from the repository URL, e.g. "vault-3f2a91c0".</summary>
    public static string VaultIdFor(string remoteUrl)
    {
        var normalized = remoteUrl.Trim().TrimEnd('/').ToLowerInvariant();
        if (normalized.EndsWith(".git", StringComparison.Ordinal)) normalized = normalized[..^4];
        var name = normalized.Split('/', '\\').Last();
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
        return (safe.Length > 0 ? safe : "vault") + "-" + hash;
    }
}

/// <summary>Opens and caches one <see cref="VaultSession"/> per registered vault.</summary>
public sealed class VaultManager : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, VaultSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public SwVaultProfile Profile { get; }

    public VaultManager(SwVaultProfile profile) => Profile = profile;

    public IReadOnlyList<VaultRegistration> Registrations => Profile.LoadRegistrations();

    public async Task<VaultSession> GetAsync(string vaultId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(vaultId, out var existing)) return existing;
            var registration = Registrations.FirstOrDefault(r => string.Equals(r.Id, vaultId, StringComparison.OrdinalIgnoreCase))
                ?? throw VaultException.NotFound($"No vault with id '{vaultId}' is set up on this PC.");
            var session = await OpenAsync(registration, ct).ConfigureAwait(false);
            _sessions[registration.Id] = session;
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VaultSession>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<VaultSession>();
        foreach (var registration in Registrations) list.Add(await GetAsync(registration.Id, ct).ConfigureAwait(false));
        return list;
    }

    /// <summary>The vault whose folder contains <paramref name="localPath"/>, if any.</summary>
    public async Task<VaultSession?> FindByLocalPathAsync(string localPath, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(localPath);
        foreach (var registration in Registrations)
        {
            var root = Path.GetFullPath(registration.LocalRoot).TrimEnd('\\') + "\\";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(full.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase))
                return await GetAsync(registration.Id, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Resolves a vault by id, or by a path inside it, or the only vault when there is just one.</summary>
    public async Task<VaultSession> ResolveAsync(string? vaultId, string? localPath, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(vaultId)) return await GetAsync(vaultId, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(localPath) && Path.IsPathFullyQualified(localPath))
        {
            var found = await FindByLocalPathAsync(localPath, ct).ConfigureAwait(false);
            if (found != null) return found;
        }
        var all = Registrations;
        if (all.Count == 1) return await GetAsync(all[0].Id, ct).ConfigureAwait(false);
        if (all.Count == 0) throw VaultException.NotFound("No vault is set up on this PC yet. Add one first (swvault vault add <url>).");
        throw VaultException.BadRequest("Several vaults are set up; say which one (--vault <id>) or run the command inside a vault folder.");
    }

    /// <summary>
    /// Connects this PC to a vault: optionally saves credentials, signs in, fetches, and adopts the
    /// vault's shared local root (so SOLIDWORKS references resolve the same way for everyone).
    /// </summary>
    public async Task<VaultSession> AddAsync(string remoteUrl, string? localRoot, Credential? credential, CancellationToken ct = default)
    {
        remoteUrl = remoteUrl.Trim();
        var id = SwVaultProfile.VaultIdFor(remoteUrl);
        if (credential != null && Uri.TryCreate(remoteUrl, UriKind.Absolute, out var server) && !server.IsFile)
            new WindowsCredentialStore(Profile.Name).Save(server, credential);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.Remove(id, out var old)) old.Dispose();
            var repoName = remoteUrl.TrimEnd('/').Split('/').Last();
            if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repoName = repoName[..^4];
            var registration = new VaultRegistration { Id = id, RemoteUrl = remoteUrl, LocalRoot = localRoot ?? Path.Combine(@"C:\SWVault", repoName) };

            var session = await OpenAsync(registration, ct).ConfigureAwait(false);
            try
            {
                await session.SignInAsync(interactive: true, ct).ConfigureAwait(false);
                await session.SyncAsync(ct: ct).ConfigureAwait(false);
                if (localRoot == null && session.Head.HasConfig
                    && !string.Equals(Path.GetFullPath(session.Config.LocalRoot), Path.GetFullPath(registration.LocalRoot), StringComparison.OrdinalIgnoreCase))
                {
                    session.Dispose();
                    registration.LocalRoot = session.Config.LocalRoot;
                    session = await OpenAsync(registration, ct).ConfigureAwait(false);
                    await session.SyncAsync(ct: ct).ConfigureAwait(false);
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }

            registration.Name = session.Head.HasConfig ? session.Config.Name : repoName;
            var all = Profile.LoadRegistrations().Where(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(registration);
            Profile.SaveRegistrations(all);
            _sessions[id] = session;
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refreshes the saved display name (e.g. after vault.json was created or renamed).</summary>
    public void UpdateName(string vaultId, string name)
    {
        var all = Profile.LoadRegistrations();
        foreach (var r in all.Where(r => string.Equals(r.Id, vaultId, StringComparison.OrdinalIgnoreCase))) r.Name = name;
        Profile.SaveRegistrations(all);
    }

    public void Remove(string vaultId)
    {
        _gate.Wait();
        try
        {
            if (_sessions.Remove(vaultId, out var session)) session.Dispose();
            Profile.SaveRegistrations(Profile.LoadRegistrations().Where(r => !string.Equals(r.Id, vaultId, StringComparison.OrdinalIgnoreCase)).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Test hook: adjust session options (fake servers, fixed identities).</summary>
    public Func<VaultSessionOptions, VaultSessionOptions>? ConfigureSession { get; set; }

    private Task<VaultSession> OpenAsync(VaultRegistration registration, CancellationToken ct)
    {
        var options = new VaultSessionOptions
        {
            VaultId = registration.Id,
            RemoteUrl = registration.RemoteUrl,
            LocalRoot = registration.LocalRoot,
            DataDir = Profile.VaultDataDir(registration.Id),
            Credentials = Profile.CredentialProvider,
        };
        return VaultSession.OpenAsync(ConfigureSession?.Invoke(options) ?? options, ct);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _gate.Dispose();
    }
}
