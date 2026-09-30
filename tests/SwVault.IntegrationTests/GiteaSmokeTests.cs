using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwVault.Core;
using SwVault.Core.Auth;
using SwVault.Core.Vault;
using SwVault.Protocol;
using static SwVault.IntegrationTests.TestVault;

namespace SwVault.IntegrationTests;

/// <summary>Runs only when SWVAULT_GITEA_TESTS=1 and scripts/dev-gitea.ps1 -Setup -Start has been run.</summary>
public sealed class GiteaFactAttribute : FactAttribute
{
    public GiteaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWVAULT_GITEA_TESTS") != "1" || !File.Exists(GiteaEnv.UsersFile))
            Skip = "Real-server test. Run scripts/dev-gitea.ps1 -Setup, then set SWVAULT_GITEA_TESTS=1.";
    }
}

internal static class GiteaEnv
{
    public static string UsersFile => Environment.GetEnvironmentVariable("SWVAULT_GITEA_USERS") ?? @"C:\SwVaultDev\gitea\dev-users.json";

    public sealed record User(string Name, string Token);

    public static (string BaseUrl, Dictionary<string, User> Users) Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(UsersFile));
        var baseUrl = doc.RootElement.GetProperty("baseUrl").GetString()!;
        var users = doc.RootElement.GetProperty("users").EnumerateArray()
            .Select(u => new User(u.GetProperty("user").GetString()!, u.GetProperty("token").GetString()!))
            .ToDictionary(u => u.Name);
        return (baseUrl, users);
    }
}

/// <summary>The whole check-out / check-in cycle against a real Gitea (git + LFS content + LFS locks).</summary>
public sealed class GiteaSmokeTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public GiteaSmokeTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private void Step(string name)
    {
        _output.WriteLine($"{_clock.Elapsed.TotalSeconds,7:N2}s  {name}");
    }

    private readonly string _repo = "smoke-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swvault-gitea", Guid.NewGuid().ToString("N")[..8]);
    private readonly List<VaultSession> _sessions = new();
    private string _baseUrl = "";
    private Dictionary<string, GiteaEnv.User> _users = new();
    private HttpClient? _admin;

    public async Task InitializeAsync()
    {
        if (!File.Exists(GiteaEnv.UsersFile) || Environment.GetEnvironmentVariable("SWVAULT_GITEA_TESTS") != "1") return;
        (_baseUrl, _users) = GiteaEnv.Load();
        _admin = new HttpClient { BaseAddress = new Uri(_baseUrl.Replace("localhost", "127.0.0.1") + "/api/v1/") };
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _users["swadmin"].Token);
        (await _admin.PostAsJsonAsync("orgs/fsae/repos", new { name = _repo, @private = true, default_branch = "main" })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"repos/fsae/{_repo}/collaborators/alice", new { permission = "admin" })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"repos/fsae/{_repo}/collaborators/bob", new { permission = "write" })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"repos/fsae/{_repo}/collaborators/carol", new { permission = "write" })).EnsureSuccessStatusCode();
    }

    private async Task<VaultSession> OpenAsync(string user)
    {
        var session = await VaultSession.OpenAsync(new VaultSessionOptions
        {
            VaultId = _repo,
            RemoteUrl = $"{_baseUrl}/fsae/{_repo}.git",
            LocalRoot = Path.Combine(_root, "ws-" + user),
            DataDir = Path.Combine(_root, "data-" + user),
            Credentials = new StaticCredentialProvider(new Credential(user, _users[user].Token)),
        });
        _sessions.Add(session);
        return session;
    }

    [GiteaFact]
    public async Task FullCycle_AgainstRealGitea()
    {
        var alice = await OpenAsync("alice");
        var bob = await OpenAsync("bob");
        var carol = await OpenAsync("carol");
        Step("sessions opened");

        var me = await alice.SignInAsync(interactive: false);
        Assert.Equal("alice", me.Login);
        Step("signed in");

        await alice.InitializeAsync(VaultConfig.CreateDefault("Smoke", alice.LocalRoot, "alice", "2025"));
        Step("vault initialized");
        var part = Write(alice, "Chassis/Tab.SLDPRT", new string('x', 200_000));
        var asm = Write(alice, "Chassis/Frame.SLDASM", "frame assembly");
        await alice.CheckInAsync(new[] { part, asm },
            new[] { new CheckInFileInfo { LocalPath = asm, References = new[] { part } } },
            new CheckInOptions { Comment = "Frame and tab" });
        Step("alice checked in 2 new files");

        // Bob gets the whole assembly; content comes from Gitea's LFS store.
        var got = await bob.GetLatestAsync(true, "Chassis/Frame.SLDASM");
        Assert.Equal(2, got.Applied.Count);
        Assert.Equal(200_000, new FileInfo(bob.LocalPathOf("Chassis/Tab.SLDPRT")).Length);
        Assert.True(IsReadOnly(bob, "Chassis/Tab.SLDPRT"));
        Step("bob got latest with references");

        // Gitea's lock API: Bob's lock blocks Alice.
        await bob.CheckOutAndApplyAsync("Chassis/Tab.SLDPRT");
        Step("bob checked out");
        var conflict = await Assert.ThrowsAsync<VaultException>(() => alice.CheckOutAsync(new[] { "Chassis/Tab.SLDPRT" }));
        Assert.Contains("bob", conflict.Message);
        Step("alice's check-out refused");

        Write(bob, "Chassis/Tab.SLDPRT", new string('y', 150_000));
        var v2 = await bob.CheckInPathsAsync("Thinner tab", "Chassis/Tab.SLDPRT");
        Assert.Contains("Chassis/Tab.SLDPRT v2", v2.NewVersions);
        Step("bob checked in v2");

        await alice.SyncAsync();
        Assert.Equal(LocalState.Outdated, (await StatusAsync(alice, "Chassis/Tab.SLDPRT")).LocalState);
        await alice.GetLatestAsync(false, "Chassis/Tab.SLDPRT");
        Assert.Equal(150_000, new FileInfo(alice.LocalPathOf("Chassis/Tab.SLDPRT")).Length);
        Step("alice synced and got v2");

        var history = await carol.GetHistoryAsync("Chassis/Tab.SLDPRT");
        Assert.Equal(new[] { 2, 1 }, history.Select(h => h.Version).ToArray());
        Step("carol read history");

        // Force unlock by a repo admin (alice) of carol's check-out.
        await carol.GetLatestAsync(true, "Chassis");
        await carol.CheckOutAndApplyAsync("Chassis/Frame.SLDASM");
        Step("carol checked out");
        await alice.ForceUnlockAsync("Chassis/Frame.SLDASM");
        await carol.SyncAsync();
        Assert.Equal(LockState.None, (await StatusAsync(carol, "Chassis/Frame.SLDASM")).LockState);
        Step("alice force-unlocked");

        // Workflow round trip on the server.
        await bob.TransitionAsync(new[] { "Chassis/Frame.SLDASM" }, "Submit for review", null);
        Step("bob submitted for review");
        await alice.TransitionAsync(new[] { "Chassis/Frame.SLDASM" }, "Approve", null);
        Step("alice approved");
        await carol.SyncAsync();
        var released = await StatusAsync(carol, "Chassis/Frame.SLDASM");
        Assert.Equal("Released", released.State);
        Assert.Equal("A", released.Revision);
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) s.Dispose();
        if (_admin != null)
        {
            await _admin.DeleteAsync($"repos/fsae/{_repo}");
            _admin.Dispose();
        }
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
