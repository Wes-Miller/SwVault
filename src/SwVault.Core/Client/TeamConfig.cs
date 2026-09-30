using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Core.Util;
using SwVault.Core.Vault;

namespace SwVault.Core.Client;

/// <summary>
/// <c>team.json</c>, shipped inside a team's install package by the server setup
/// (server/linux/setup.sh). It tells a fresh install which vault to join, so members only
/// sign in with a user name and password.
/// </summary>
public sealed class TeamConfig
{
    public const string FileName = "team.json";

    public string Name { get; set; } = "Vault";
    public string VaultUrl { get; set; } = "";
    public string LocalRoot { get; set; } = @"C:\SWVault\Vault";
    public string? SolidworksVersion { get; set; }

    /// <summary>Server login that initializes the vault on first sign-in and becomes its admin.</summary>
    public string? Admin { get; set; }

    /// <summary>Download every file right after the first sign-in.</summary>
    public bool DownloadAllOnJoin { get; set; } = true;

    /// <summary>team.json next to the program, one folder up (package root), or in %ProgramData%\SwVault.</summary>
    public static TeamConfig? Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(AppContext.BaseDirectory, "..", FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SwVault", FileName),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var config = Json.Deserialize<TeamConfig>(File.ReadAllText(path));
            if (config != null && !string.IsNullOrWhiteSpace(config.VaultUrl)) return config;
        }
        return null;
    }
}

public static class TeamJoin
{
    /// <summary>
    /// Signs in to the team's vault with a user name and password: creates a SwVault access token
    /// on the server (so the password is never stored), connects the vault, and initializes it
    /// when the team admin signs in to a brand-new vault.
    /// </summary>
    public static async Task<VaultSession> JoinAsync(VaultManager manager, TeamConfig team, string userName, string password, CancellationToken ct = default)
    {
        var token = await CreateTokenAsync(new Uri(team.VaultUrl), userName, password, ct).ConfigureAwait(false);
        var session = await manager.AddAsync(team.VaultUrl, team.LocalRoot, new Credential(userName, token), ct).ConfigureAwait(false);
        if (!session.Head.HasConfig)
        {
            var login = session.User?.Login ?? userName;
            if (!string.IsNullOrEmpty(team.Admin) && !string.Equals(team.Admin, login, StringComparison.OrdinalIgnoreCase))
                throw VaultException.Conflict($"The vault hasn't been set up yet. Ask {team.Admin} to sign in first, then try again.");
            var config = VaultConfig.CreateDefault(team.Name, team.LocalRoot, login, team.SolidworksVersion);
            await session.InitializeAsync(config, ct).ConfigureAwait(false);
            await session.SyncAsync(ct: ct).ConfigureAwait(false);
            manager.UpdateName(session.VaultId, team.Name);
        }
        return session;
    }

    /// <summary>Gitea: POST /api/v1/users/{user}/tokens with the password (HTTP Basic).</summary>
    public static async Task<string> CreateTokenAsync(Uri vaultUrl, string userName, string password, CancellationToken ct)
    {
        var apiBase = new Uri(HostAdapters.ParseRemote(vaultUrl).Base, "api/v1/");
        var tokenName = $"SwVault {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmmss}";
        var body = JsonSerializer.Serialize(new { name = tokenName, scopes = new[] { "write:repository", "read:user", "read:organization" } });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, $"users/{Uri.EscapeDataString(userName)}/tokens"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwVault", "0.1"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw VaultException.Offline($"Can't reach the vault server at {vaultUrl.Host}. Check your internet connection ({ex.Message}).", ex);
        }
        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw VaultException.Unauthorized("Wrong user name or password.");
            if (!response.IsSuccessStatusCode)
                throw new VaultException(Protocol.ErrorCodes.Internal, $"The server couldn't create an access token (HTTP {(int)response.StatusCode}): {raw}");
            using var doc = JsonDocument.Parse(raw);
            var token = HostAdapters.GetString(doc.RootElement, "sha1") ?? HostAdapters.GetString(doc.RootElement, "token");
            return string.IsNullOrEmpty(token) ? throw new VaultException(Protocol.ErrorCodes.Internal, "The server's token response had no token.") : token;
        }
    }
}
