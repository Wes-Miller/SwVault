using System.Net;
using SwVault.Core.Auth;
using SwVault.Protocol;

namespace SwVault.Core.Lfs;

public sealed record LockCreateResult(LfsLock? Created, LfsLock? Existing, string? Message)
{
    public bool Success => Created != null;
}

/// <summary>Git LFS File Locking API: this is what "check out" means on the server.</summary>
public sealed class LockClient
{
    private readonly LfsHttp _api;
    private readonly string _refName;

    public LockClient(HttpClient http, Uri lfsBase, AuthContext auth, string refName = "refs/heads/main")
    {
        _api = new LfsHttp(http, lfsBase, auth);
        _refName = refName;
    }

    public async Task<LockCreateResult> CreateAsync(string path, CancellationToken ct)
    {
        var body = new CreateLockRequest { Path = path, Ref = new LfsRef { Name = _refName } };
        var (status, parsed, raw) = await _api.SendAsync<LockResponse>(HttpMethod.Post, "locks", body, ct).ConfigureAwait(false);
        return status switch
        {
            HttpStatusCode.Created or HttpStatusCode.OK when parsed?.Lock != null => new LockCreateResult(parsed.Lock, null, null),
            HttpStatusCode.Conflict => new LockCreateResult(null, parsed?.Lock, parsed?.Message ?? "Already locked."),
            HttpStatusCode.Forbidden => throw VaultException.Forbidden($"You don't have write access to this vault, so you can't check out {path}."),
            _ => throw LfsHttp.Failure($"check out {path}", status, raw),
        };
    }

    public async Task<IReadOnlyList<LfsLock>> ListAllAsync(CancellationToken ct)
    {
        var all = new List<LfsLock>();
        string? cursor = null;
        do
        {
            var query = "locks?limit=100&refspec=" + Uri.EscapeDataString(_refName) + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            var (status, parsed, raw) = await _api.SendAsync<LockListResponse>(HttpMethod.Get, query, null, ct).ConfigureAwait(false);
            if (status != HttpStatusCode.OK || parsed == null) throw LfsHttp.Failure("list checked-out files", status, raw);
            all.AddRange(parsed.Locks);
            cursor = string.IsNullOrEmpty(parsed.NextCursor) || parsed.Locks.Count == 0 ? null : parsed.NextCursor;
        } while (cursor != null);
        return all;
    }

    /// <summary>
    /// Splits locks into ours and theirs as the server sees them. Returns null when the server
    /// refuses (read-only users get 403), in which case callers fall back to comparing owner names.
    /// </summary>
    public async Task<(List<LfsLock> Ours, List<LfsLock> Theirs)?> VerifyAsync(CancellationToken ct)
    {
        var ours = new List<LfsLock>();
        var theirs = new List<LfsLock>();
        string? cursor = null;
        do
        {
            var body = new VerifyLocksRequest { Ref = new LfsRef { Name = _refName }, Cursor = cursor, Limit = 100 };
            var (status, parsed, raw) = await _api.SendAsync<VerifyLocksResponse>(HttpMethod.Post, "locks/verify", body, ct).ConfigureAwait(false);
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
            if (status != HttpStatusCode.OK || parsed == null) throw LfsHttp.Failure("verify checked-out files", status, raw);
            ours.AddRange(parsed.Ours);
            theirs.AddRange(parsed.Theirs);
            cursor = string.IsNullOrEmpty(parsed.NextCursor) || parsed.Ours.Count + parsed.Theirs.Count == 0 ? null : parsed.NextCursor;
        } while (cursor != null);
        return (ours, theirs);
    }

    public async Task UnlockAsync(string id, bool force, CancellationToken ct)
    {
        var body = new UnlockRequest { Force = force, Ref = new LfsRef { Name = _refName } };
        var (status, _, raw) = await _api.SendAsync<LockResponse>(HttpMethod.Post, $"locks/{Uri.EscapeDataString(id)}/unlock", body, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK || status == HttpStatusCode.NotFound) return; // gone already is fine
        if (status == HttpStatusCode.Forbidden)
            throw VaultException.Forbidden(force ? "Only vault admins can release another user's check-out." : "That check-out belongs to someone else.");
        throw LfsHttp.Failure("release the check-out", status, raw);
    }
}
