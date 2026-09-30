using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Protocol;

namespace SwVault.Core.Client;

/// <summary>General member, or lead of a subteam (e.g. "Chassis").</summary>
public sealed record TeamProfile(bool IsLead, string? Subteam)
{
    public override string ToString() => IsLead ? $"{Subteam} lead" : "General member";
}

public sealed record TeamPerson(string Login, string? FullName, TeamProfile? Profile)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Login : FullName!;
}

public sealed record TeamDirectory(IReadOnlyList<TeamPerson> People, IReadOnlyList<string> Subteams)
{
    public IEnumerable<TeamPerson> Leads => People.Where(p => p.Profile?.IsLead == true);
}

public sealed record InviteDescription(string Team, string Role, IReadOnlyList<string> EmailDomains, IReadOnlyList<string> Subteams)
{
    public bool NeedsEmail => EmailDomains.Count > 0;
}

public sealed record InviteInfo(string Code, string Role, int UsesLeft, int Uses, DateTimeOffset Expires, string? Note, string? CreatedBy);

/// <summary>
/// Client for the server's invite service (server/linux/invites/invites.py), published next to
/// Gitea at https://&lt;server&gt;/swvault-invites/. People join with a code; vault admins make codes.
/// </summary>
public static partial class TeamInvites
{
    public static Uri ServiceUrl(string vaultUrl) =>
        new(HostAdapters.ParseRemote(new Uri(vaultUrl)).Base, "swvault-invites/");

    public static Uri DownloadUrl(string vaultUrl, string? code = null) =>
        new(ServiceUrl(vaultUrl), "download" + (string.IsNullOrEmpty(code) ? "" : "?invite=" + Uri.EscapeDataString(code)));

    /// <summary>"k7qm r3xt 9bwe" / "K7QM-R3XT-9BWE" / "...-invite-K7QM-R3XT-9BWE" -> "K7QM-R3XT-9BWE", else null.</summary>
    public static string? NormalizeCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CodePattern().Match(text.ToUpperInvariant());
        if (!match.Success) return null;
        var raw = Regex.Replace(match.Value, "[^A-Z0-9]", "");
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}";
    }

    [GeneratedRegex(@"[2-9A-HJ-NP-Z]{4}[-\s]?[2-9A-HJ-NP-Z]{4}[-\s]?[2-9A-HJ-NP-Z]{4}")]
    private static partial Regex CodePattern();

    /// <summary>
    /// Creates the account for an invite code, with the member's profile (general member, or lead of
    /// a subteam). When the server requires a school email, <paramref name="email"/> and the code
    /// from <see cref="SendEmailCodeAsync"/> are required. Afterwards sign in with <see cref="TeamJoin.JoinAsync"/>.
    /// </summary>
    public static async Task RedeemAsync(string vaultUrl, string code, string userName, string password, string? fullName,
        string? email, string? emailCode, TeamProfile profile, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, "redeem", null, new
        {
            code = NormalizeCode(code) ?? code, username = userName, password, fullName, email, emailCode,
            memberType = profile.IsLead ? "lead" : "member", subteam = profile.Subteam,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Emails a 6-digit code to prove the member owns <paramref name="email"/>.</summary>
    public static async Task SendEmailCodeAsync(string vaultUrl, string code, string email, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, "verify", null, new { code = NormalizeCode(code) ?? code, email }, ct).ConfigureAwait(false);
    }

    /// <summary>What an invite code is for, or null when it isn't valid.</summary>
    public static async Task<InviteDescription?> DescribeAsync(string vaultUrl, string code, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "invite/" + Uri.EscapeDataString(NormalizeCode(code) ?? code), null, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("valid", out var valid) || !valid.GetBoolean()) return null;
        return new InviteDescription(
            HostAdapters.GetString(root, "team") ?? "",
            HostAdapters.GetString(root, "role") ?? "designer",
            Strings(root, "emailDomains"),
            Strings(root, "subteams"));
    }

    /// <summary>Everyone on the team with their profile, leads first.</summary>
    public static async Task<TeamDirectory> PeopleAsync(string vaultUrl, Credential me, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "people", me, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var people = root.GetProperty("people").EnumerateArray().Select(p => new TeamPerson(
            HostAdapters.GetString(p, "login") ?? "",
            HostAdapters.GetString(p, "fullName"),
            HostAdapters.GetString(p, "memberType") switch
            {
                "lead" => new TeamProfile(true, HostAdapters.GetString(p, "subteam")),
                "member" => new TeamProfile(false, null),
                _ => null,
            })).ToList();
        return new TeamDirectory(people, Strings(root, "subteams"));
    }

    public static async Task SaveMyProfileAsync(string vaultUrl, Credential me, TeamProfile profile, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Put, vaultUrl, "people/me", me,
            new { memberType = profile.IsLead ? "lead" : "member", subteam = profile.Subteam }, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the server to email whoever a review event concerns (lead or requester). Once per event.</summary>
    public static async Task NotifyReviewAsync(string vaultUrl, Credential me, int reviewNumber, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, $"reviews/{reviewNumber}/notify", me, new { }, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : Array.Empty<string>();

    public static async Task<InviteInfo> CreateAsync(string vaultUrl, Credential admin, string role, int uses, int days, string? note, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, vaultUrl, "invites", admin, new { role, uses, days, note }, ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    public static async Task<IReadOnlyList<InviteInfo>> ListAsync(string vaultUrl, Credential admin, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "invites", admin, null, ct).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(Parse).ToList();
    }

    public static async Task RevokeAsync(string vaultUrl, Credential admin, string code, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, vaultUrl, "invites/" + Uri.EscapeDataString(code), admin, null, ct).ConfigureAwait(false);
    }

    /// <summary>Ready-to-paste invitation for a team chat or email.</summary>
    public static string Message(string teamName, string vaultUrl, InviteInfo invite)
    {
        var access = invite.Role == "viewer" ? "view and download" : "check out and check in";
        var uses = invite.Uses == 1 ? "for one person" : $"for up to {invite.Uses} people";
        return $"""
            You're invited to the {teamName} vault (SwVault PDM for SOLIDWORKS).

            1. Download the installer: {DownloadUrl(vaultUrl, invite.Code)}
            2. Close SOLIDWORKS. Right-click the zip > Extract All, then double-click "Install SwVault.cmd".
            3. Choose a user name and password when SwVault asks. Invite code (if asked): {invite.Code}
            Your files then download by themselves. Open SOLIDWORKS and use the SwVault tab.

            (Access: {access}. Works {uses} until {invite.Expires.ToLocalTime():MMM d}.)
            """;
    }

    private static InviteInfo Parse(JsonElement e) => new(
        HostAdapters.GetString(e, "code") ?? "",
        HostAdapters.GetString(e, "role") ?? "designer",
        e.TryGetProperty("usesLeft", out var left) ? left.GetInt32() : 0,
        e.TryGetProperty("uses", out var uses) ? uses.GetInt32() : 0,
        DateTimeOffset.TryParse(HostAdapters.GetString(e, "expires"), out var expires) ? expires : DateTimeOffset.MinValue,
        HostAdapters.GetString(e, "note"),
        HostAdapters.GetString(e, "createdBy"));

    private static async Task<JsonDocument> SendAsync(HttpMethod method, string vaultUrl, string path, Credential? auth, object? body, CancellationToken ct)
    {
        var uri = new Uri(ServiceUrl(vaultUrl), path);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwVault", "0.1"));
        if (auth != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.UserName}:{auth.Secret}")));
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw VaultException.Offline($"Can't reach the vault server at {uri.Host}. Check your internet connection ({ex.Message}).", ex);
        }
        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
            }
            catch (JsonException)
            {
            }
            if (response.IsSuccessStatusCode && doc != null) return doc;

            var message = doc != null && doc.RootElement.ValueKind == JsonValueKind.Object ? HostAdapters.GetString(doc.RootElement, "error") : null;
            doc?.Dispose();
            if (message == null && response.StatusCode == HttpStatusCode.NotFound)
                message = "This vault server doesn't have invites yet. Ask your admin to update it (run server/linux/setup.sh again).";
            var code = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => ErrorCodes.BadRequest,
                HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
                HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
                HttpStatusCode.NotFound => ErrorCodes.NotFound,
                HttpStatusCode.Conflict => ErrorCodes.Conflict,
                HttpStatusCode.TooManyRequests => ErrorCodes.BadRequest,
                _ => ErrorCodes.Internal,
            };
            throw new VaultException(code, message ?? $"The invite service answered HTTP {(int)response.StatusCode}.");
        }
    }
}
