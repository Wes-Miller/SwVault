using System.Text.RegularExpressions;

namespace SwVault.Core.Util;

/// <summary>
/// Vault paths are relative to the vault root, use forward slashes and keep the case stored
/// on the server. Windows is case-insensitive, so all lookups use <see cref="Comparer"/>.
/// </summary>
public static class PathRules
{
    public const string InternalDir = ".swvault";
    public const string MetaPrefix = ".swvault/meta/";
    public const string ConfigPath = ".swvault/vault.json";
    public const string GitAttributesPath = ".gitattributes";
    public const string LocalDirName = ".swvault-local";

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public static string Normalize(string vaultPath)
    {
        var p = vaultPath.Replace('\\', '/').Trim();
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        return p.Trim('/');
    }

    /// <summary>Paths SwVault manages itself; never shown as user files.</summary>
    public static bool IsInternal(string vaultPath)
    {
        var p = Normalize(vaultPath);
        return p.Equals(GitAttributesPath, Comparison)
            || p.Equals(InternalDir, Comparison)
            || p.StartsWith(InternalDir + "/", Comparison)
            || p.Equals(LocalDirName, Comparison)
            || p.StartsWith(LocalDirName + "/", Comparison);
    }

    public static string MetaPathFor(string vaultPath) => MetaPrefix + Normalize(vaultPath) + ".json";

    public static string? FileForMetaPath(string metaPath)
    {
        if (!metaPath.StartsWith(MetaPrefix, StringComparison.Ordinal) || !metaPath.EndsWith(".json", StringComparison.Ordinal))
            return null;
        return metaPath.Substring(MetaPrefix.Length, metaPath.Length - MetaPrefix.Length - ".json".Length);
    }

    /// <summary>Maps an absolute local path to a vault path, or null when it is outside the root.</summary>
    public static string? ToVaultPath(string localRoot, string absolutePath)
    {
        var root = Path.GetFullPath(localRoot).TrimEnd('\\', '/') + "\\";
        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch (Exception)
        {
            return null;
        }
        if (!full.StartsWith(root, Comparison)) return null;
        var rel = Normalize(full.Substring(root.Length));
        return rel.Length == 0 ? null : rel;
    }

    public static string ToLocalPath(string localRoot, string vaultPath) =>
        Path.Combine(localRoot, Normalize(vaultPath).Replace('/', '\\'));

    public static string GetFolder(string vaultPath)
    {
        var p = Normalize(vaultPath);
        var i = p.LastIndexOf('/');
        return i < 0 ? "" : p.Substring(0, i);
    }

    public static string GetFileName(string vaultPath)
    {
        var p = Normalize(vaultPath);
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p.Substring(i + 1);
    }

    public static string Combine(string folder, string name) =>
        string.IsNullOrEmpty(folder) ? Normalize(name) : Normalize(folder) + "/" + Normalize(name);

    /// <summary>True for folders that a cloud sync client owns; those fight with SwVault over file locks.</summary>
    public static bool IsUnderSyncedFolder(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", Comparison)) return true;
        }
        var markers = new[] { "\\OneDrive\\", "\\OneDrive - ", "\\Dropbox\\", "\\Google Drive\\", "\\My Drive\\", "\\iCloudDrive\\" };
        return markers.Any(m => full.Contains(m, Comparison));
    }
}

/// <summary>
/// Minimal gitignore-style matching: '*' and '?' within one segment; a trailing '/' means
/// "a folder with this name anywhere"; a pattern without '/' matches the file name at any depth.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<(Regex Regex, bool DirOnly, bool FullPath)> _rules = new();

    public IgnoreRules(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
            var dirOnly = pattern.EndsWith('/');
            pattern = pattern.Trim('/');
            var fullPath = pattern.Contains('/');
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$";
            _rules.Add((new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), dirOnly, fullPath));
        }
    }

    public bool IsIgnored(string vaultPath)
    {
        var p = PathRules.Normalize(vaultPath);
        var segments = p.Split('/');
        foreach (var (regex, dirOnly, fullPath) in _rules)
        {
            if (fullPath)
            {
                if (regex.IsMatch(p)) return true;
                continue;
            }
            if (dirOnly)
            {
                for (var i = 0; i < segments.Length - 1; i++)
                    if (regex.IsMatch(segments[i])) return true;
                continue;
            }
            if (regex.IsMatch(segments[^1])) return true;
        }
        return false;
    }
}
