using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Protocol;

namespace SwVault.Core.Client;

public enum ReviewKind
{
    Design,
    Simulation,
    Drawing,
}

public enum ReviewStatus
{
    Waiting,
    ChangesRequested,
    Approved,
    Cancelled,
}

public sealed record ReviewComment(string Author, DateTimeOffset At, string Text);

public sealed record ReviewRequest(
    int Number,
    ReviewKind Kind,
    string Path,
    int Version,
    string Requester,
    string Lead,
    ReviewStatus Status,
    string Message,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    string WebUrl)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string KindText => Kind switch
    {
        ReviewKind.Simulation => "Simulation review",
        ReviewKind.Drawing => "Drawing review",
        _ => "Design review",
    };

    public string StatusText => Status switch
    {
        ReviewStatus.ChangesRequested => "Changes requested",
        ReviewStatus.Approved => "Approved",
        ReviewStatus.Cancelled => "Cancelled",
        _ => "Waiting for review",
    };
}

public enum ReviewDecision
{
    Comment,
    Approve,
    RequestChanges,
    Cancel,
}

/// <summary>
/// Review requests: a member asks a subteam lead for a design, simulation or drawing review of a
/// vault file. Each request is an issue in the vault repository, assigned to the lead and labeled
/// "review" + its kind + its status, so it also shows up (with notifications and discussion) in the
/// server's web UI. The first line of the issue body carries the file and version as JSON.
/// </summary>
public static partial class Reviews
{
    public const string MarkerLabel = "review";
    private const string KindPrefix = "review: ";
    private const string ApprovedLabel = "review: approved";
    private const string ChangesLabel = "review: changes requested";
    private const string CancelledLabel = "review: cancelled";

    /// <summary>Labels setup.sh creates (and <see cref="EnsureLabelsAsync"/> fills in if missing), with colors.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [MarkerLabel] = "#6f42c1",
        ["review: design"] = "#0366d6",
        ["review: simulation"] = "#0e8a16",
        ["review: drawing"] = "#d93f0b",
        [ApprovedLabel] = "#2cbe4e",
        [ChangesLabel] = "#fbca04",
        [CancelledLabel] = "#8b949e",
    };

    public static string LabelFor(ReviewKind kind) => KindPrefix + kind.ToString().ToLowerInvariant();

    /// <summary>A sensible default kind for a file: drawings get drawing reviews.</summary>
    public static ReviewKind DefaultKindFor(string path) =>
        System.IO.Path.GetExtension(path).Equals(".slddrw", StringComparison.OrdinalIgnoreCase) ? ReviewKind.Drawing : ReviewKind.Design;

    public static async Task<ReviewRequest> RequestAsync(string vaultUrl, Credential me, ReviewKind kind, string lead,
        string vaultPath, int version, string? message, CancellationToken ct = default)
    {
        var labels = await EnsureLabelsAsync(vaultUrl, me, ct).ConfigureAwait(false);
        var header = JsonSerializer.Serialize(new { kind = kind.ToString().ToLowerInvariant(), path = vaultPath, version });
        var kindText = kind switch { ReviewKind.Simulation => "Simulation", ReviewKind.Drawing => "Drawing", _ => "Design" };
        var body = new StringBuilder()
            .Append("<!-- swvault-review ").Append(header).AppendLine(" -->")
            .Append("**").Append(kindText).Append(" review** requested by @").Append(me.UserName)
            .Append(" for `").Append(vaultPath).Append("` (version ").Append(version).AppendLine(").")
            .AppendLine();
        if (!string.IsNullOrWhiteSpace(message))
            foreach (var line in message.Trim().Split('\n')) body.Append("> ").AppendLine(line.TrimEnd('\r'));
        body.AppendLine().AppendLine("_Respond from SOLIDWORKS (SwVault tab > Reviews) or comment here._");

        using var doc = await SendAsync(HttpMethod.Post, vaultUrl, me, "issues", new
        {
            title = $"{kindText} review: {System.IO.Path.GetFileName(vaultPath)} v{version}",
            body = body.ToString(),
            assignees = new[] { lead },
            labels = new[] { labels[MarkerLabel], labels[LabelFor(kind)] },
        }, ct).ConfigureAwait(false);
        return Parse(doc.RootElement) ?? throw new VaultException(ErrorCodes.Internal, "The server's answer didn't look like a review request.");
    }

    /// <summary>Review requests assigned to me (as a lead) or opened by me, newest activity first.</summary>
    public static async Task<IReadOnlyList<ReviewRequest>> ListAsync(string vaultUrl, Credential me, bool assignedToMe, bool includeClosed, CancellationToken ct = default)
    {
        var who = assignedToMe ? "assigned_by" : "created_by";
        var state = includeClosed ? "all" : "open";
        var result = new List<ReviewRequest>();
        for (var page = 1; page <= 10; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, vaultUrl, me,
                $"issues?type=issues&state={state}&labels={MarkerLabel}&{who}={Uri.EscapeDataString(me.UserName)}&limit=50&page={page}", null, ct).ConfigureAwait(false);
            var batch = doc.RootElement.EnumerateArray().Select(Parse).Where(r => r != null).Select(r => r!).ToList();
            result.AddRange(batch);
            if (doc.RootElement.GetArrayLength() < 50) break;
        }
        return result.OrderByDescending(r => r.Updated).ToList();
    }

    public static async Task<IReadOnlyList<ReviewComment>> CommentsAsync(string vaultUrl, Credential me, int number, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, me, $"issues/{number}/comments", null, ct).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(c => new ReviewComment(
            c.TryGetProperty("user", out var u) ? HostAdapters.GetString(u, "login") ?? "" : "",
            DateTimeOffset.TryParse(HostAdapters.GetString(c, "created_at"), out var at) ? at : DateTimeOffset.MinValue,
            HostAdapters.GetString(c, "body") ?? "")).ToList();
    }

    /// <summary>
    /// The lead approves (closes it) or asks for changes (stays open); anyone comments; the requester
    /// cancels. A comment is posted for every decision so the history reads naturally on the web too.
    /// </summary>
    public static async Task RespondAsync(string vaultUrl, Credential me, ReviewRequest review, ReviewDecision decision, string? comment, CancellationToken ct = default)
    {
        var text = decision switch
        {
            ReviewDecision.Approve => "✅ **Approved**",
            ReviewDecision.RequestChanges => "✏️ **Changes requested**",
            ReviewDecision.Cancel => "Request cancelled",
            _ => "",
        };
        if (!string.IsNullOrWhiteSpace(comment)) text = text.Length == 0 ? comment.Trim() : text + "\n\n" + comment.Trim();
        if (text.Length == 0) throw VaultException.BadRequest("Write a comment first.");
        using (await SendAsync(HttpMethod.Post, vaultUrl, me, $"issues/{review.Number}/comments", new { body = text }, ct).ConfigureAwait(false)) { }
        if (decision == ReviewDecision.Comment) return;

        var labels = await EnsureLabelsAsync(vaultUrl, me, ct).ConfigureAwait(false);
        var keep = new List<long> { labels[MarkerLabel], labels[LabelFor(review.Kind)] };
        var status = decision switch
        {
            ReviewDecision.Approve => ApprovedLabel,
            ReviewDecision.RequestChanges => ChangesLabel,
            _ => CancelledLabel,
        };
        keep.Add(labels[status]);
        using (await SendAsync(HttpMethod.Put, vaultUrl, me, $"issues/{review.Number}/labels", new { labels = keep }, ct).ConfigureAwait(false)) { }
        var open = decision == ReviewDecision.RequestChanges;
        using (await SendAsync(HttpMethod.Patch, vaultUrl, me, $"issues/{review.Number}", new { state = open ? "open" : "closed" }, ct).ConfigureAwait(false)) { }
    }

    /// <summary>Label name -> id, creating any that are missing (first request on a fresh vault).</summary>
    public static async Task<Dictionary<string, long>> EnsureLabelsAsync(string vaultUrl, Credential me, CancellationToken ct = default)
    {
        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var doc = await SendAsync(HttpMethod.Get, vaultUrl, me, "labels?limit=100", null, ct).ConfigureAwait(false))
            foreach (var l in doc.RootElement.EnumerateArray())
                found[HostAdapters.GetString(l, "name") ?? ""] = l.GetProperty("id").GetInt64();
        foreach (var (name, color) in Labels)
        {
            if (found.ContainsKey(name)) continue;
            using var created = await SendAsync(HttpMethod.Post, vaultUrl, me, "labels", new { name, color, description = "SwVault review requests" }, ct).ConfigureAwait(false);
            found[name] = created.RootElement.GetProperty("id").GetInt64();
        }
        return found;
    }

    [GeneratedRegex(@"<!--\s*swvault-review\s+(\{.*?\})\s*-->", RegexOptions.Singleline)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^\s*>\s?(.*)$", RegexOptions.Multiline)]
    private static partial Regex QuotePattern();

    internal static ReviewRequest? Parse(JsonElement issue)
    {
        var body = HostAdapters.GetString(issue, "body") ?? "";
        var header = HeaderPattern().Match(body);
        if (!header.Success) return null;
        string kindText = "design", path = "";
        var version = 0;
        try
        {
            using var h = JsonDocument.Parse(header.Groups[1].Value);
            kindText = HostAdapters.GetString(h.RootElement, "kind") ?? kindText;
            path = HostAdapters.GetString(h.RootElement, "path") ?? "";
            version = h.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (JsonException)
        {
            return null;
        }
        var kind = Enum.TryParse<ReviewKind>(kindText, ignoreCase: true, out var k) ? k : ReviewKind.Design;
        var labelNames = issue.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array
            ? ls.EnumerateArray().Select(l => HostAdapters.GetString(l, "name") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        var closed = HostAdapters.GetString(issue, "state") == "closed";
        var status = labelNames.Contains(ApprovedLabel) ? ReviewStatus.Approved
            : labelNames.Contains(CancelledLabel) ? ReviewStatus.Cancelled
            : closed ? ReviewStatus.Cancelled
            : labelNames.Contains(ChangesLabel) ? ReviewStatus.ChangesRequested
            : ReviewStatus.Waiting;
        var lead = issue.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
            ? HostAdapters.GetString(a[0], "login") ?? ""
            : "";
        var message = string.Join("\n", QuotePattern().Matches(body).Select(m => m.Groups[1].Value));
        return new ReviewRequest(
            issue.GetProperty("number").GetInt32(), kind, path, version,
            issue.TryGetProperty("user", out var u) ? HostAdapters.GetString(u, "login") ?? "" : "",
            lead, status, message,
            DateTimeOffset.TryParse(HostAdapters.GetString(issue, "created_at"), out var created) ? created : DateTimeOffset.MinValue,
            DateTimeOffset.TryParse(HostAdapters.GetString(issue, "updated_at"), out var updated) ? updated : DateTimeOffset.MinValue,
            HostAdapters.GetString(issue, "html_url") ?? "");
    }

    private static async Task<JsonDocument> SendAsync(HttpMethod method, string vaultUrl, Credential me, string path, object? body, CancellationToken ct)
    {
        var (baseUri, owner, repo) = HostAdapters.ParseRemote(new Uri(vaultUrl));
        var uri = new Uri(baseUri, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/{path}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwVault", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{me.UserName}:{me.Secret}")));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw VaultException.Offline($"Can't reach the vault server at {uri.Host} ({ex.Message}).", ex);
        }
        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your saved sign-in doesn't allow review requests. Sign in again (tray icon > Vaults...).",
                HttpStatusCode.Forbidden => "You don't have permission to do that on this vault. (Viewers can't request reviews; ask an admin.)",
                HttpStatusCode.NotFound => "The vault or review wasn't found on the server.",
                HttpStatusCode.UnprocessableEntity => "The server didn't accept that (is the lead still on the team?): " + raw,
                _ => $"The server answered HTTP {(int)response.StatusCode}: {raw}",
            };
            var code = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
                HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
                HttpStatusCode.NotFound => ErrorCodes.NotFound,
                _ => ErrorCodes.BadRequest,
            };
            throw new VaultException(code, message);
        }
    }
}
