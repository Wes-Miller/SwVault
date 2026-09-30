using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwVault.Core.Auth;
using SwVault.Protocol;

namespace SwVault.Core.Hosts;

public sealed record HostUser(string Login, string? FullName, string? Email)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Login : FullName!;
}

/// <summary>
/// The only server-specific code: who am I, and create a repository. Everything else
/// (git, LFS content, LFS locks) is the standard protocol that Gitea and GitHub both implement.
/// </summary>
public interface IHostAdapter
{
    string Kind { get; }
    Task<HostUser> GetCurrentUserAsync(CancellationToken ct);
    Task CreateRepositoryAsync(string owner, string name, bool isPrivate, CancellationToken ct);
}

public static class HostAdapters
{
    public static IHostAdapter Create(Uri remote, HttpClient http, AuthContext auth) =>
        remote.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GitHubHost(http, auth)
            : new GiteaHost(remote, http, auth);

    /// <summary>Standard LFS endpoint discovery: &lt;remote&gt;.git/info/lfs.</summary>
    public static Uri LfsUrlFor(string remoteUrl)
    {
        var url = remoteUrl.TrimEnd('/');
        return new Uri(url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url + "/info/lfs" : url + ".git/info/lfs");
    }

    /// <summary>Splits "https://host/[prefix/]owner/repo(.git)" into (server base, owner, repo).</summary>
    public static (Uri Base, string Owner, string Repo) ParseRemote(Uri remote)
    {
        var segments = remote.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) throw VaultException.BadRequest($"'{remote}' is not a repository URL (expected .../owner/repo).");
        var repo = segments[^1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        var owner = segments[^2];
        var prefix = string.Join('/', segments.Take(segments.Length - 2));
        var baseUri = new Uri(remote.GetLeftPart(UriPartial.Authority) + "/" + (prefix.Length > 0 ? prefix + "/" : ""));
        return (baseUri, owner, repo);
    }

    internal static async Task<JsonDocument> SendJsonAsync(HttpClient http, AuthContext auth, HttpMethod method, Uri uri, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwVault", "0.1"));
            var header = await auth.GetBasicHeaderAsync(ct).ConfigureAwait(false);
            if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw VaultException.Offline($"The vault server is unreachable ({ex.Message}).", ex);
            }
            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (attempt == 0 && header != null)
                    {
                        await auth.InvalidateAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    throw VaultException.Unauthorized("Not signed in to the vault server, or the saved token has expired.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    var code = response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
                        HttpStatusCode.NotFound => ErrorCodes.NotFound,
                        HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity => ErrorCodes.Conflict,
                        _ => ErrorCodes.Internal,
                    };
                    throw new VaultException(code, $"Server request {uri.AbsolutePath} failed (HTTP {(int)response.StatusCode}): {raw}");
                }
                return JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
            }
        }
    }

    internal static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class GiteaHost : IHostAdapter
{
    private readonly HttpClient _http;
    private readonly AuthContext _auth;
    private readonly Uri _apiBase;

    public GiteaHost(Uri remote, HttpClient http, AuthContext auth)
    {
        _http = http;
        _auth = auth;
        _apiBase = new Uri(HostAdapters.ParseRemote(remote).Base, "api/v1/");
    }

    public string Kind => "gitea";

    public async Task<HostUser> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await HostAdapters.SendJsonAsync(_http, _auth, HttpMethod.Get, new Uri(_apiBase, "user"), null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new HostUser(HostAdapters.GetString(root, "login") ?? "", HostAdapters.GetString(root, "full_name"), HostAdapters.GetString(root, "email"));
    }

    public async Task CreateRepositoryAsync(string owner, string name, bool isPrivate, CancellationToken ct)
    {
        var me = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        var path = string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase) ? "user/repos" : $"orgs/{Uri.EscapeDataString(owner)}/repos";
        var body = new Dictionary<string, object> { ["name"] = name, ["private"] = isPrivate, ["default_branch"] = "main", ["auto_init"] = false };
        using var _ = await HostAdapters.SendJsonAsync(_http, _auth, HttpMethod.Post, new Uri(_apiBase, path), body, ct).ConfigureAwait(false);
    }
}

public sealed class GitHubHost : IHostAdapter
{
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private readonly HttpClient _http;
    private readonly AuthContext _auth;

    public GitHubHost(HttpClient http, AuthContext auth)
    {
        _http = http;
        _auth = auth;
    }

    public string Kind => "github";

    public async Task<HostUser> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await HostAdapters.SendJsonAsync(_http, _auth, HttpMethod.Get, new Uri(ApiBase, "user"), null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new HostUser(HostAdapters.GetString(root, "login") ?? "", HostAdapters.GetString(root, "name"), HostAdapters.GetString(root, "email"));
    }

    public async Task CreateRepositoryAsync(string owner, string name, bool isPrivate, CancellationToken ct)
    {
        var me = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        var path = string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase) ? "user/repos" : $"orgs/{Uri.EscapeDataString(owner)}/repos";
        var body = new Dictionary<string, object> { ["name"] = name, ["private"] = isPrivate, ["auto_init"] = false };
        using var _ = await HostAdapters.SendJsonAsync(_http, _auth, HttpMethod.Post, new Uri(ApiBase, path), body, ct).ConfigureAwait(false);
    }
}

/// <summary>Fixed identity, for tests and servers without a user API.</summary>
public sealed class StaticHostAdapter : IHostAdapter
{
    private readonly HostUser _user;

    public StaticHostAdapter(HostUser user) => _user = user;

    public string Kind => "static";

    public Task<HostUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(_user);

    public Task CreateRepositoryAsync(string owner, string name, bool isPrivate, CancellationToken ct) => Task.CompletedTask;
}
