using System.Text.Json.Serialization;

namespace SwVault.Core.Lfs;

// Wire models for the Git LFS batch and locking APIs
// (https://github.com/git-lfs/git-lfs/blob/main/docs/api).

internal sealed class BatchRequest
{
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("transfers")] public List<string> Transfers { get; set; } = new() { "basic" };
    [JsonPropertyName("ref")] public LfsRef? Ref { get; set; }
    [JsonPropertyName("objects")] public List<LfsObjectSpec> Objects { get; set; } = new();
    [JsonPropertyName("hash_algo")] public string HashAlgo { get; set; } = "sha256";
}

internal sealed class LfsRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal sealed class LfsObjectSpec
{
    [JsonPropertyName("oid")] public string Oid { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

internal sealed class BatchResponse
{
    [JsonPropertyName("transfer")] public string? Transfer { get; set; }
    [JsonPropertyName("objects")] public List<BatchObject> Objects { get; set; } = new();
}

internal sealed class BatchObject
{
    [JsonPropertyName("oid")] public string Oid { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("authenticated")] public bool? Authenticated { get; set; }
    [JsonPropertyName("actions")] public Dictionary<string, BatchAction>? Actions { get; set; }
    [JsonPropertyName("error")] public BatchError? Error { get; set; }
}

internal sealed class BatchAction
{
    [JsonPropertyName("href")] public string Href { get; set; } = "";
    [JsonPropertyName("header")] public Dictionary<string, string>? Header { get; set; }
    [JsonPropertyName("expires_in")] public long? ExpiresIn { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

internal sealed class BatchError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class LfsLock
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("locked_at")] public string? LockedAt { get; set; }
    [JsonPropertyName("owner")] public LfsLockOwner? Owner { get; set; }

    [JsonIgnore] public string OwnerName => Owner?.Name ?? "";
}

public sealed class LfsLockOwner
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal sealed class CreateLockRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("ref")] public LfsRef? Ref { get; set; }
}

internal sealed class LockResponse
{
    [JsonPropertyName("lock")] public LfsLock? Lock { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

internal sealed class LockListResponse
{
    [JsonPropertyName("locks")] public List<LfsLock> Locks { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

internal sealed class VerifyLocksRequest
{
    [JsonPropertyName("ref")] public LfsRef? Ref { get; set; }
    [JsonPropertyName("cursor")] public string? Cursor { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; } = 100;
}

internal sealed class VerifyLocksResponse
{
    [JsonPropertyName("ours")] public List<LfsLock> Ours { get; set; } = new();
    [JsonPropertyName("theirs")] public List<LfsLock> Theirs { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

internal sealed class UnlockRequest
{
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("ref")] public LfsRef? Ref { get; set; }
}
