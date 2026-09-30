using System.Globalization;
using System.Text;

namespace SwVault.Core.Vault;

/// <summary>A Git LFS pointer file (the small text blob git stores in place of the real content).</summary>
public readonly record struct LfsPointer(string Oid, long Size)
{
    public const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    public string ToText() =>
        $"{VersionLine}\noid sha256:{Oid}\nsize {Size.ToString(CultureInfo.InvariantCulture)}\n";

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(ToText());

    public static bool TryParse(ReadOnlySpan<byte> content, out LfsPointer pointer)
    {
        pointer = default;
        if (content.Length > 1024 || content.Length < 40) return false;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(content);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (!text.StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal)
            && !text.StartsWith("version https://hawser.github.com/spec/", StringComparison.Ordinal))
            return false;

        string? oid = null;
        long size = -1;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
                oid = line.Substring("oid sha256:".Length).Trim();
            else if (line.StartsWith("size ", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                size = s;
        }
        if (oid == null || oid.Length != 64 || size < 0) return false;
        pointer = new LfsPointer(oid.ToLowerInvariant(), size);
        return true;
    }
}
