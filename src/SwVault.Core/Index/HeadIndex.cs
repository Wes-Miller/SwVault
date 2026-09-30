using System.Globalization;
using System.Text;
using SwVault.Core.Git;
using SwVault.Core.State;
using SwVault.Core.Util;
using SwVault.Core.Vault;

namespace SwVault.Core.Index;

/// <summary>One user file in the server's head commit.</summary>
public sealed class HeadFile
{
    public required string Path { get; init; }
    public required string BlobSha { get; init; }
    public bool IsLfs { get; init; }

    /// <summary>LFS sha256 oid, or "git:&lt;blob sha&gt;" for content committed without LFS.</summary>
    public required string Oid { get; init; }
    public long Size { get; init; }
    public FileMeta? Meta { get; init; }

    /// <summary>Files added outside SwVault have no sidecar; they count as version 1.</summary>
    public int Version => Meta?.Version > 0 ? Meta.Version : 1;
}

/// <summary>Snapshot of the vault at the server's head commit, built from the pointer-only mirror.</summary>
public sealed class HeadIndex
{
    public const string RawOidPrefix = "git:";

    public string? Commit { get; }
    public VaultConfig Config { get; }
    public bool HasConfig { get; }
    public IReadOnlyDictionary<string, HeadFile> Files { get; }

    /// <summary>Child path -> parent paths that reference it (from sidecar references).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> WhereUsed { get; }
    public IReadOnlySet<string> Folders { get; }

    private HeadIndex(string? commit, VaultConfig config, bool hasConfig, Dictionary<string, HeadFile> files)
    {
        Commit = commit;
        Config = config;
        HasConfig = hasConfig;
        Files = files;

        var whereUsed = new Dictionary<string, List<string>>(PathRules.Comparer);
        var folders = new HashSet<string>(PathRules.Comparer);
        foreach (var file in files.Values)
        {
            var folder = PathRules.GetFolder(file.Path);
            while (folder.Length > 0 && folders.Add(folder)) folder = PathRules.GetFolder(folder);
            foreach (var reference in file.Meta?.References ?? new List<ReferenceEntry>())
            {
                if (!whereUsed.TryGetValue(reference.Path, out var parents)) whereUsed[reference.Path] = parents = new List<string>();
                if (!parents.Contains(file.Path, PathRules.Comparer)) parents.Add(file.Path);
            }
        }
        WhereUsed = whereUsed.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, PathRules.Comparer);
        Folders = folders;
    }

    public static HeadIndex Empty(VaultConfig config) => new(null, config, false, new Dictionary<string, HeadFile>(PathRules.Comparer));

    internal static HeadIndex FromFiles(IEnumerable<HeadFile> files, VaultConfig config, string? commit = "test") =>
        new(commit, config, true, files.ToDictionary(f => f.Path, PathRules.Comparer));

    public HeadFile? Get(string vaultPath) => Files.TryGetValue(PathRules.Normalize(vaultPath), out var f) ? f : null;

    public static async Task<HeadIndex> BuildAsync(GitMirror mirror, StateStore state, string? commit, VaultConfig fallbackConfig, CancellationToken ct)
    {
        if (commit == null) return Empty(fallbackConfig);

        var entries = await mirror.LsTreeAsync(commit, ct).ConfigureAwait(false);
        var cache = state.LoadBlobCache();
        var newCache = new List<(string, BlobKind, string)>();

        var config = fallbackConfig;
        var hasConfig = false;
        var metaBlobs = new Dictionary<string, string>(PathRules.Comparer);
        var fileBlobs = new List<TreeEntry>();
        foreach (var entry in entries)
        {
            if (entry.Type != "blob") continue;
            if (string.Equals(entry.Path, PathRules.ConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await mirror.ReadBlobAsync(entry.Sha, ct).ConfigureAwait(false);
                try
                {
                    config = (bytes == null ? null : Json.Deserialize<VaultConfig>(bytes)) ?? fallbackConfig;
                    hasConfig = bytes != null;
                }
                catch (System.Text.Json.JsonException)
                {
                    config = fallbackConfig; // a broken vault.json must not take the whole vault offline
                }
                continue;
            }
            var metaFor = PathRules.FileForMetaPath(entry.Path);
            if (metaFor != null)
            {
                metaBlobs[metaFor] = entry.Sha;
                continue;
            }
            if (PathRules.IsInternal(entry.Path)) continue;
            fileBlobs.Add(entry);
        }

        var files = new Dictionary<string, HeadFile>(PathRules.Comparer);
        foreach (var entry in fileBlobs)
        {
            string oid;
            long size;
            bool isLfs;
            if (cache.TryGetValue(entry.Sha, out var cached) && cached.Kind is BlobKind.Pointer or BlobKind.Raw)
            {
                (isLfs, oid, size) = DecodeCached(cached.Kind, cached.Data, entry.Sha);
            }
            else
            {
                var bytes = await mirror.ReadBlobAsync(entry.Sha, ct).ConfigureAwait(false) ?? Array.Empty<byte>();
                if (LfsPointer.TryParse(bytes, out var pointer))
                {
                    (isLfs, oid, size) = (true, pointer.Oid, pointer.Size);
                    newCache.Add((entry.Sha, BlobKind.Pointer, $"{pointer.Oid} {pointer.Size.ToString(CultureInfo.InvariantCulture)}"));
                }
                else
                {
                    (isLfs, oid, size) = (false, RawOidPrefix + entry.Sha, bytes.LongLength);
                    newCache.Add((entry.Sha, BlobKind.Raw, bytes.LongLength.ToString(CultureInfo.InvariantCulture)));
                }
            }

            FileMeta? meta = null;
            if (metaBlobs.TryGetValue(entry.Path, out var metaSha))
            {
                string? json = null;
                if (cache.TryGetValue(metaSha, out var cachedMeta) && cachedMeta.Kind == BlobKind.Meta)
                {
                    json = cachedMeta.Data;
                }
                else
                {
                    var bytes = await mirror.ReadBlobAsync(metaSha, ct).ConfigureAwait(false);
                    if (bytes != null)
                    {
                        json = Encoding.UTF8.GetString(bytes);
                        newCache.Add((metaSha, BlobKind.Meta, json));
                    }
                }
                meta = TryParseMeta(json);
            }

            files[entry.Path] = new HeadFile { Path = entry.Path, BlobSha = entry.Sha, IsLfs = isLfs, Oid = oid, Size = size, Meta = meta };
        }

        if (newCache.Count > 0) state.PutBlobCache(newCache);
        return new HeadIndex(commit, config, hasConfig, files);
    }

    internal static FileMeta? TryParseMeta(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return Json.Deserialize<FileMeta>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static (bool IsLfs, string Oid, long Size) DecodeCached(BlobKind kind, string data, string blobSha)
    {
        if (kind == BlobKind.Pointer)
        {
            var parts = data.Split(' ');
            return (true, parts[0], long.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        return (false, RawOidPrefix + blobSha, long.Parse(data, CultureInfo.InvariantCulture));
    }
}
