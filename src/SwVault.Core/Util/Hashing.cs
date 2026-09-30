using System.Security.Cryptography;

namespace SwVault.Core.Util;

public static class Hashing
{
    /// <summary>Streams the file through SHA-256 (the LFS oid) without loading it into memory.</summary>
    public static async Task<(string Oid, long Size)> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return (Convert.ToHexStringLower(hash), stream.Length);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
