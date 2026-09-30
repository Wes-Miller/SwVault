namespace SwVault.Core.Git;

/// <summary>
/// Which git to run: SWVAULT_GIT, else the MinGit bundled with the installed package
/// (&lt;app&gt;\git\cmd\git.exe), else git from PATH (Git for Windows).
/// </summary>
public static class GitExe
{
    private static readonly Lazy<string> Resolved = new(Resolve);

    public static string Path => Resolved.Value;

    public static bool IsBundled => !string.Equals(Path, "git", StringComparison.Ordinal);

    private static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("SWVAULT_GIT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
        return File.Exists(bundled) ? bundled : "git";
    }
}
