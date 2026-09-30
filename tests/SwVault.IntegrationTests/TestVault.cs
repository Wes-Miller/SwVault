using System.Diagnostics;
using System.Text;
using SwVault.Core;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.IntegrationTests;

/// <summary>
/// A throwaway vault: a local bare git repo as the "server" repository, the fake LFS server for
/// content and locks, and one workspace + data folder per simulated user.
/// </summary>
public sealed class TestVault : IAsyncDisposable
{
    private readonly List<VaultSession> _sessions = new();

    public string Root { get; }
    public string RemotePath { get; }
    public FakeLfsServer Server { get; }

    private TestVault(string root, string remotePath, FakeLfsServer server)
    {
        Root = root;
        RemotePath = remotePath;
        Server = server;
    }

    public static async Task<TestVault> CreateAsync(bool initialize = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "swvault-tests", Guid.NewGuid().ToString("N")[..10]);
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(root);
        RunGit("init", "--bare", "--initial-branch=main", remote);
        var server = await FakeLfsServer.StartAsync();
        server.AddAdmin("alice");
        var vault = new TestVault(root, remote, server);
        if (initialize)
        {
            var alice = await vault.OpenAsync("alice");
            var config = VaultConfig.CreateDefault("Test", alice.LocalRoot, "alice", "2025");
            await alice.InitializeAsync(config);
        }
        return vault;
    }

    private readonly Dictionary<string, VaultSession> _byUser = new();

    /// <summary>Returns the user's session, opening it on first use. A dataSuffix opens a second PC for the same user.</summary>
    public async Task<VaultSession> OpenAsync(string user, string? dataSuffix = null)
    {
        var key = user + "|" + dataSuffix;
        if (_byUser.TryGetValue(key, out var existing)) return existing;
        var session = await VaultSession.OpenAsync(new VaultSessionOptions
        {
            VaultId = "test",
            RemoteUrl = RemotePath,
            LocalRoot = Path.Combine(Root, "ws-" + user + (dataSuffix ?? "")),
            DataDir = Path.Combine(Root, "data-" + user + (dataSuffix ?? "")),
            Credentials = new StaticCredentialProvider(new Credential(user, "secret")),
            Host = new StaticHostAdapter(new HostUser(user, char.ToUpperInvariant(user[0]) + user[1..], user + "@example.com")),
            LfsUrl = new Uri(Server.LfsUrl()),
            AllowSyncedFolderRoot = true,
        });
        _sessions.Add(session);
        _byUser[key] = session;
        return session;
    }

    /// <summary>Closes and reopens a user's session (simulates restarting the agent).</summary>
    public async Task<VaultSession> ReopenAsync(string user)
    {
        var key = user + "|";
        if (_byUser.Remove(key, out var old))
        {
            _sessions.Remove(old);
            old.Dispose();
        }
        return await OpenAsync(user);
    }

    public static string Write(VaultSession session, string vaultPath, string content)
    {
        var path = session.LocalPathOf(vaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // Make sure the change is visible to size/mtime checks even within the same clock tick.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
        return path;
    }

    public static string Read(VaultSession session, string vaultPath) => File.ReadAllText(session.LocalPathOf(vaultPath), Encoding.UTF8);

    public static bool IsReadOnly(VaultSession session, string vaultPath) =>
        File.GetAttributes(session.LocalPathOf(vaultPath)).HasFlag(FileAttributes.ReadOnly);

    public static async Task<FileStatusDto> StatusAsync(VaultSession session, string vaultPath) =>
        await session.GetStatusAsync(vaultPath) ?? throw new InvalidOperationException($"No status for {vaultPath}");

    private static void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + p.StandardError.ReadToEnd());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions) s.Dispose();
        await Server.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A git process may still hold a handle for a moment; temp cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class SessionTestExtensions
{
    public static async Task<ApplyResult> GetLatestAsync(this VaultSession s, bool withReferences, params string[] paths)
    {
        var op = await s.PrepareGetLatestAsync(paths, withReferences);
        return await s.ApplyAsync(op);
    }

    public static async Task<StagedOperation> CheckOutAndApplyAsync(this VaultSession s, params string[] paths)
    {
        var op = await s.CheckOutAsync(paths);
        await s.ApplyAsync(op);
        return op;
    }

    public static Task<CheckInResult> CheckInPathsAsync(this VaultSession s, string comment, params string[] paths) =>
        s.CheckInAsync(paths, null, new CheckInOptions { Comment = comment });
}
