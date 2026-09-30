using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SwVault.IntegrationTests;

/// <summary>
/// In-process Git LFS server implementing the batch (basic transfer) and locking APIs,
/// with users taken from Basic auth. Mirrors how Gitea/GitHub behave for the calls SwVault makes.
/// </summary>
public sealed class FakeLfsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();
    private readonly List<LockEntry> _locks = new();
    private readonly object _lockSync = new();
    private readonly HashSet<string> _admins = new(StringComparer.OrdinalIgnoreCase);
    private int _nextLockId;
    private int _uploads;

    private sealed record LockEntry(string Id, string Path, string Owner, string LockedAt);

    public string BaseUrl { get; private set; } = "";
    public int Uploads => Volatile.Read(ref _uploads);

    private FakeLfsServer(WebApplication app) => _app = app;

    public static async Task<FakeLfsServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeLfsServer(app);
        server.Map(app);
        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return server;
    }

    public string LfsUrl(string owner = "team", string repo = "vault") => $"{BaseUrl}/{owner}/{repo}.git/info/lfs";

    public void AddAdmin(string user)
    {
        lock (_lockSync) _admins.Add(user);
    }

    public IReadOnlyList<(string Path, string Owner)> CurrentLocks
    {
        get
        {
            lock (_lockSync) return _locks.Select(l => (l.Path, l.Owner)).ToList();
        }
    }

    /// <summary>Same strict check as Gitea's CheckAcceptMediaType: the first Accept entry must be the LFS type.</summary>
    private static bool AcceptsLfs(HttpContext ctx) =>
        ctx.Request.Headers.Accept.ToString().Split(';')[0] == "application/vnd.git-lfs+json";

    private static string? UserOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)) return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6))).Split(':')[0];
    }

    private void Map(WebApplication app)
    {
        const string prefix = "/{owner}/{repo}.git/info/lfs";

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var isObjectTransfer = path.Contains("/objects/", StringComparison.Ordinal) && !path.EndsWith("/objects/batch", StringComparison.Ordinal);
            if (!isObjectTransfer && !AcceptsLfs(ctx))
            {
                ctx.Response.StatusCode = 415;
                await ctx.Response.WriteAsync("{\"Message\":\"Unsupported Media Type\"}");
                return;
            }
            await next();
        });

        app.MapPost(prefix + "/objects/batch", async (HttpContext ctx) =>
        {
            if (UserOf(ctx) == null) return Results.Unauthorized();
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            var operation = body!["operation"]!.GetValue<string>();
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path.Value!.Replace("/objects/batch", "")}";
            var auth = ctx.Request.Headers.Authorization.ToString();
            var result = new JsonArray();
            foreach (var obj in body["objects"]!.AsArray())
            {
                var oid = obj!["oid"]!.GetValue<string>();
                var entry = new JsonObject { ["oid"] = oid, ["size"] = obj["size"]!.GetValue<long>() };
                if (operation == "upload")
                {
                    if (!_objects.ContainsKey(oid))
                    {
                        entry["actions"] = new JsonObject
                        {
                            ["upload"] = new JsonObject { ["href"] = $"{baseUrl}/objects/{oid}", ["header"] = new JsonObject { ["Authorization"] = auth } },
                            ["verify"] = new JsonObject
                            {
                                ["href"] = $"{baseUrl}/verify",
                                ["header"] = new JsonObject { ["Authorization"] = auth, ["Accept"] = "application/vnd.git-lfs+json" },
                            },
                        };
                    }
                }
                else if (_objects.ContainsKey(oid))
                {
                    // No header on purpose: the client must add its own credentials for the LFS host.
                    entry["actions"] = new JsonObject { ["download"] = new JsonObject { ["href"] = $"{baseUrl}/objects/{oid}" } };
                }
                else
                {
                    entry["error"] = new JsonObject { ["code"] = 404, ["message"] = "Object does not exist" };
                }
                result.Add(entry);
            }
            return Json(new JsonObject { ["transfer"] = "basic", ["objects"] = result });
        });

        app.MapPut(prefix + "/objects/{oid}", async (HttpContext ctx, string oid) =>
        {
            if (UserOf(ctx) == null) return Results.Unauthorized();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != oid) return Results.BadRequest("oid mismatch");
            _objects[oid] = bytes;
            Interlocked.Increment(ref _uploads);
            return Results.Ok();
        });

        app.MapGet(prefix + "/objects/{oid}", (HttpContext ctx, string oid) =>
        {
            if (UserOf(ctx) == null) return Results.Unauthorized();
            return _objects.TryGetValue(oid, out var bytes) ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound();
        });

        app.MapPost(prefix + "/verify", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            return _objects.ContainsKey(body!["oid"]!.GetValue<string>()) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost(prefix + "/locks", async (HttpContext ctx) =>
        {
            var user = UserOf(ctx);
            if (user == null) return Results.Unauthorized();
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            var path = body!["path"]!.GetValue<string>();
            lock (_lockSync)
            {
                var existing = _locks.FirstOrDefault(l => l.Path == path);
                if (existing != null)
                    return Json(new JsonObject { ["lock"] = ToJson(existing), ["message"] = "already created lock" }, 409);
                var entry = new LockEntry((++_nextLockId).ToString(), path, user, DateTimeOffset.UtcNow.ToString("O"));
                _locks.Add(entry);
                return Json(new JsonObject { ["lock"] = ToJson(entry) }, 201);
            }
        });

        app.MapGet(prefix + "/locks", (HttpContext ctx) =>
        {
            if (UserOf(ctx) == null) return Results.Unauthorized();
            var path = ctx.Request.Query["path"].ToString();
            lock (_lockSync)
            {
                var list = new JsonArray();
                foreach (var l in _locks.Where(l => path.Length == 0 || l.Path == path)) list.Add(ToJson(l));
                return Json(new JsonObject { ["locks"] = list });
            }
        });

        app.MapPost(prefix + "/locks/verify", (HttpContext ctx) =>
        {
            var user = UserOf(ctx);
            if (user == null) return Results.Unauthorized();
            lock (_lockSync)
            {
                var ours = new JsonArray();
                var theirs = new JsonArray();
                foreach (var l in _locks) (l.Owner == user ? ours : theirs).Add(ToJson(l));
                return Json(new JsonObject { ["ours"] = ours, ["theirs"] = theirs });
            }
        });

        app.MapPost(prefix + "/locks/{id}/unlock", async (HttpContext ctx, string id) =>
        {
            var user = UserOf(ctx);
            if (user == null) return Results.Unauthorized();
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            var force = body?["force"]?.GetValue<bool>() ?? false;
            lock (_lockSync)
            {
                var entry = _locks.FirstOrDefault(l => l.Id == id);
                if (entry == null) return Results.NotFound();
                if (entry.Owner != user && !(force && _admins.Contains(user))) return Json(new JsonObject { ["message"] = "not your lock" }, 403);
                _locks.Remove(entry);
                return Json(new JsonObject { ["lock"] = ToJson(entry) });
            }
        });
    }

    private static JsonObject ToJson(LockEntry l) => new()
    {
        ["id"] = l.Id,
        ["path"] = l.Path,
        ["locked_at"] = l.LockedAt,
        ["owner"] = new JsonObject { ["name"] = l.Owner },
    };

    private static IResult Json(JsonNode node, int status = 200) =>
        Results.Text(node.ToJsonString(new JsonSerializerOptions()), "application/vnd.git-lfs+json", Encoding.UTF8, status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
