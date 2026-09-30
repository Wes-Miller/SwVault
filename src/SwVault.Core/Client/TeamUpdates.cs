using System.Security.Cryptography;
using System.Text.Json;
using SwVault.Core.Hosts;
using SwVault.Protocol;

namespace SwVault.Core.Client;

/// <summary>The installer an admin published on the team server (scripts/package.ps1 -Publish).</summary>
public sealed record InstallerRelease(Version Version, string Sha256, long Size, DateTimeOffset Published, bool Required, string? Notes)
{
    /// <summary>True when this release is newer than <paramref name="current"/> (compared as major.minor.build).</summary>
    public bool IsNewerThan(Version current) => Version > Normalize(current);

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

public static partial class TeamInvites
{
    /// <summary>The published installer, or null when there is none (or the server predates updates).</summary>
    public static async Task<InstallerRelease?> LatestInstallerAsync(string vaultUrl, CancellationToken ct = default)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "installer/latest", null, null, ct).ConfigureAwait(false);
            return ParseRelease(doc.RootElement);
        }
        catch (VaultException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return null;
        }
    }

    internal static InstallerRelease? ParseRelease(JsonElement e)
    {
        if (!Version.TryParse(HostAdapters.GetString(e, "version"), out var version)) return null;
        var sha = HostAdapters.GetString(e, "sha256");
        if (string.IsNullOrEmpty(sha)) return null;
        return new InstallerRelease(
            InstallerRelease.Normalize(version),
            sha.ToLowerInvariant(),
            e.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : 0,
            DateTimeOffset.TryParse(HostAdapters.GetString(e, "published"), out var published) ? published : DateTimeOffset.MinValue,
            e.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            HostAdapters.GetString(e, "notes") is { Length: > 0 } notes ? notes : null);
    }

    /// <summary>
    /// Downloads the published installer zip to <paramref name="target"/> and checks it against the
    /// release's SHA-256; a mismatch (a newer upload mid-download, or a damaged file) deletes it and throws.
    /// </summary>
    public static async Task DownloadInstallerAsync(string vaultUrl, InstallerRelease release, string target,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(DownloadUrl(vaultUrl), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw VaultException.Offline($"Couldn't download the update ({ex.Message}).", ex);
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new VaultException(ErrorCodes.Internal, $"Couldn't download the update (HTTP {(int)response.StatusCode}).");
                var total = response.Content.Headers.ContentLength ?? release.Size;
                using var sha = SHA256.Create();
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var file = File.Create(partial))
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (actual != release.Sha256)
                {
                    File.Delete(partial);
                    throw new VaultException(ErrorCodes.Internal, "The downloaded update didn't match what the server published (it may have just been replaced). Try again.");
                }
            }
        }
        File.Move(partial, target, overwrite: true);
    }
}
