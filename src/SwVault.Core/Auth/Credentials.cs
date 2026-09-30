using System.Runtime.InteropServices;
using System.Text;
using SwVault.Core.Git;

namespace SwVault.Core.Auth;

public sealed record Credential(string UserName, string Secret);

public interface ICredentialProvider
{
    /// <summary>Returns a credential for the server, or null if none is available.</summary>
    Task<Credential?> GetAsync(Uri server, bool interactive, CancellationToken ct);

    /// <summary>Called when the server refused the credential (e.g. expired token).</summary>
    Task RejectAsync(Uri server, Credential credential, CancellationToken ct);
}

/// <summary>Fixed credential, e.g. from SWVAULT_USER / SWVAULT_TOKEN for scripts and tests.</summary>
public sealed class StaticCredentialProvider : ICredentialProvider
{
    private readonly Credential _credential;

    public StaticCredentialProvider(Credential credential) => _credential = credential;

    public Task<Credential?> GetAsync(Uri server, bool interactive, CancellationToken ct) => Task.FromResult<Credential?>(_credential);

    public Task RejectAsync(Uri server, Credential credential, CancellationToken ct) => Task.CompletedTask;

    public static StaticCredentialProvider? FromEnvironment()
    {
        var user = Environment.GetEnvironmentVariable("SWVAULT_USER");
        var token = Environment.GetEnvironmentVariable("SWVAULT_TOKEN");
        return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(token) ? null : new StaticCredentialProvider(new Credential(user, token));
    }
}

/// <summary>
/// Tokens saved by SwVault itself in Windows Credential Manager (target "SwVault:&lt;server&gt;").
/// Used for servers where Git Credential Manager can't help, e.g. a Gitea on plain HTTP.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialProvider
{
    private readonly string _profile;

    public WindowsCredentialStore(string profile = "default") => _profile = profile;

    private string TargetFor(Uri server) =>
        (_profile == "default" ? "SwVault:" : $"SwVault[{_profile}]:") + server.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    public Task<Credential?> GetAsync(Uri server, bool interactive, CancellationToken ct) => Task.FromResult(Read(TargetFor(server)));

    public Task RejectAsync(Uri server, Credential credential, CancellationToken ct) => Task.CompletedTask;

    public void Save(Uri server, Credential credential) => Write(TargetFor(server), credential);

    public bool Delete(Uri server) => NativeMethods.CredDelete(TargetFor(server), NativeMethods.CRED_TYPE_GENERIC, 0);

    private static Credential? Read(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
            var secret = cred.CredentialBlobSize > 0
                ? Encoding.Unicode.GetString(ReadBytes(cred.CredentialBlob, (int)cred.CredentialBlobSize))
                : "";
            return new Credential(cred.UserName ?? "", secret);
        }
        finally
        {
            NativeMethods.CredFree(ptr);
        }
    }

    private static byte[] ReadBytes(IntPtr ptr, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

    private static void Write(string target, Credential credential)
    {
        var blob = Encoding.Unicode.GetBytes(credential.Secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = credential.UserName,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!NativeMethods.CredWrite(ref cred, 0))
                throw new VaultException(Protocol.ErrorCodes.Internal, $"Could not save the credential (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}

/// <summary>
/// Asks Git Credential Manager (via <c>git credential fill</c>). GCM shows the browser sign-in
/// for GitHub, GitLab, and Gitea over HTTPS and stores the token in Windows Credential Manager.
/// </summary>
public sealed class GitCredentialProvider : ICredentialProvider
{
    public async Task<Credential?> GetAsync(Uri server, bool interactive, CancellationToken ct)
    {
        var input = $"protocol={server.Scheme}\nhost={server.Authority}\n\n";
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = interactive ? "1" : "0",
            ["GCM_INTERACTIVE"] = interactive ? "auto" : "never",
        };
        var result = await ProcessRunner.RunAsync("git", new[] { "credential", "fill" }, environment: env,
            stdin: Encoding.UTF8.GetBytes(input), timeout: interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        var values = Parse(result.StdOutText);
        return values.TryGetValue("password", out var secret) && !string.IsNullOrEmpty(secret)
            ? new Credential(values.GetValueOrDefault("username") ?? "", secret)
            : null;
    }

    public async Task RejectAsync(Uri server, Credential credential, CancellationToken ct)
    {
        var input = $"protocol={server.Scheme}\nhost={server.Authority}\nusername={credential.UserName}\npassword={credential.Secret}\n\n";
        await ProcessRunner.RunAsync("git", new[] { "credential", "reject" }, stdin: Encoding.UTF8.GetBytes(input),
            timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1).TrimEnd('\r');
        }
        return values;
    }
}

/// <summary>Tries each provider in order: environment, SwVault's own stored token, then GCM.</summary>
public sealed class CredentialChain : ICredentialProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _providers;

    public CredentialChain(params ICredentialProvider?[] providers) =>
        _providers = providers.Where(p => p != null).Select(p => p!).ToList();

    public async Task<Credential?> GetAsync(Uri server, bool interactive, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            var credential = await provider.GetAsync(server, interactive, ct).ConfigureAwait(false);
            if (credential != null) return credential;
        }
        return null;
    }

    public async Task RejectAsync(Uri server, Credential credential, CancellationToken ct)
    {
        foreach (var provider in _providers) await provider.RejectAsync(server, credential, ct).ConfigureAwait(false);
    }

    public static CredentialChain Default(string profile = "default") =>
        new(StaticCredentialProvider.FromEnvironment(), new WindowsCredentialStore(profile), new GitCredentialProvider());
}

/// <summary>Holds the current credential for one server and turns it into HTTP and git auth.</summary>
public sealed class AuthContext
{
    private readonly ICredentialProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Credential? _credential;

    public Uri Server { get; }

    public AuthContext(Uri server, ICredentialProvider provider)
    {
        Server = server;
        _provider = provider;
    }

    public async Task<Credential?> GetCredentialAsync(bool interactive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _credential ??= await _provider.GetAsync(Server, interactive, ct).ConfigureAwait(false);
            return _credential;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credential != null) await _provider.RejectAsync(Server, _credential, ct).ConfigureAwait(false);
            _credential = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetBasicHeaderAsync(CancellationToken ct)
    {
        var credential = await GetCredentialAsync(interactive: false, ct).ConfigureAwait(false);
        return credential == null ? null : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Secret}"));
    }

    /// <summary>
    /// Git environment that injects the auth header for this server only (via GIT_CONFIG_*,
    /// so the token never appears on a command line) and disables git's own credential helpers.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> GitEnvironmentAsync(CancellationToken ct)
    {
        var header = await GetBasicHeaderAsync(ct).ConfigureAwait(false);
        var env = new Dictionary<string, string?>();
        if (header == null || Server.IsFile || Server.Scheme == "file") return env;
        var prefix = Server.GetLeftPart(UriPartial.Authority) + "/";
        env["GIT_CONFIG_COUNT"] = "2";
        env["GIT_CONFIG_KEY_0"] = $"http.{prefix}.extraHeader";
        env["GIT_CONFIG_VALUE_0"] = "Authorization: " + header;
        env["GIT_CONFIG_KEY_1"] = "credential.helper";
        env["GIT_CONFIG_VALUE_1"] = "";
        return env;
    }
}
