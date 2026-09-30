using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwVault.Core.Auth;
using SwVault.Protocol;

namespace SwVault.Core.Lfs;

/// <summary>JSON calls to the LFS API with auth, one re-auth attempt on 401, and error mapping.</summary>
internal sealed class LfsHttp
{
    public const string MediaType = "application/vnd.git-lfs+json";
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly AuthContext _auth;

    public Uri BaseUri { get; }

    public LfsHttp(HttpClient http, Uri baseUri, AuthContext auth)
    {
        _http = http;
        _auth = auth;
        BaseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");
    }

    public Uri Resolve(string relative) => new(BaseUri, relative);

    public async Task<(HttpStatusCode Status, TResponse? Body, string Raw)> SendAsync<TResponse>(
        HttpMethod method, string relative, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, Resolve(relative));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
            var header = await _auth.GetBasicHeaderAsync(ct).ConfigureAwait(false);
            if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaType);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
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
                        await _auth.InvalidateAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    throw VaultException.Unauthorized("Not signed in to the vault server, or the saved token has expired.");
                }
                TResponse? parsed = default;
                if (raw.Length > 0 && raw.TrimStart().StartsWith('{'))
                {
                    try { parsed = JsonSerializer.Deserialize<TResponse>(raw, JsonOptions); } catch (JsonException) { }
                }
                return (response.StatusCode, parsed, raw);
            }
        }
    }

    /// <summary>Headers for a transfer action; adds our credentials only for the LFS server's own host.</summary>
    public async Task ApplyActionHeadersAsync(HttpRequestMessage request, BatchAction action, CancellationToken ct)
    {
        var hasAuth = false;
        if (action.Header != null)
        {
            foreach (var (key, value) in action.Header)
            {
                if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasAuth = true;
                // Replace rather than append: servers such as Gitea compare Accept exactly and reject
                // "application/vnd.git-lfs+json, application/vnd.git-lfs+json" with 415.
                request.Headers.Remove(key);
                if (!request.Headers.TryAddWithoutValidation(key, value) && request.Content != null)
                {
                    request.Content.Headers.Remove(key);
                    request.Content.Headers.TryAddWithoutValidation(key, value);
                }
            }
        }
        if (!hasAuth && request.RequestUri != null
            && string.Equals(request.RequestUri.Authority, BaseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            var header = await _auth.GetBasicHeaderAsync(ct).ConfigureAwait(false);
            if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
        }
    }

    public static VaultException Failure(string action, HttpStatusCode status, string raw)
    {
        var message = raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? raw;
        }
        catch (JsonException)
        {
        }
        var code = status switch
        {
            HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
            HttpStatusCode.NotFound => ErrorCodes.NotFound,
            HttpStatusCode.Conflict => ErrorCodes.Conflict,
            _ => ErrorCodes.Internal,
        };
        return new VaultException(code, $"Could not {action} (HTTP {(int)status}): {message}");
    }
}
