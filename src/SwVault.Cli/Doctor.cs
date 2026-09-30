using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SwVault.Core;
using SwVault.Core.Client;
using SwVault.Core.Git;
using SwVault.Core.Util;
using SwVault.Protocol;

namespace SwVault.Cli;

/// <summary>Setup checks, so "it doesn't work" turns into one actionable line.</summary>
internal static class Doctor
{
    private static int _problems;

    public static async Task RunAsync(VaultManager manager, CancellationToken ct)
    {
        _problems = 0;
        Console.WriteLine("SwVault doctor");
        Console.WriteLine($"  profile: {manager.Profile.Name} ({manager.Profile.BaseDir})");

        var git = await ProcessRunner.RunAsync("git", new[] { "--version" }, ct: ct);
        var match = Regex.Match(git.StdOutText, @"(\d+)\.(\d+)");
        var gitOk = git.ExitCode == 0 && match.Success && (int.Parse(match.Groups[1].Value) > 2 || int.Parse(match.Groups[2].Value) >= 31);
        Report(gitOk, "Git for Windows 2.31 or newer", git.ExitCode == 0 ? git.StdOutText.Trim() : "git not found on PATH - install Git for Windows");

        var sw = SolidworksVersion();
        Report(sw != null, "SOLIDWORKS installed", sw ?? "not found (only the add-in needs it)");

        using (var addins = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks\Addins\{" + ProtocolInfo.AddInGuid + "}"))
            Report(addins != null, "SwVault add-in registered", addins != null ? "yes" : "no - run the installer (or scripts\\dev-register-addin.ps1 as administrator)");

        var pipeExists = Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith(manager.Profile.PipeName, StringComparison.OrdinalIgnoreCase));
        Report(pipeExists, "SwVault agent running", pipeExists ? "yes" : "no - it starts with SOLIDWORKS or at sign-in");

        var registrations = manager.Registrations;
        Report(registrations.Count > 0, "Vaults set up", registrations.Count == 0 ? "none - run 'swvault vault add <url>'" : registrations.Count.ToString());
        foreach (var registration in registrations)
        {
            Console.WriteLine($"  vault {registration.Name} ({registration.Id})");
            Report(!PathRules.IsUnderSyncedFolder(registration.LocalRoot), "    folder is not cloud-synced", registration.LocalRoot);
            VaultSession session;
            try
            {
                session = await manager.GetAsync(registration.Id, ct);
            }
            catch (VaultException ex)
            {
                Report(false, "    opens", ex.Message);
                continue;
            }
            try
            {
                var user = await session.SignInAsync(interactive: false, ct);
                Report(true, "    signed in", user.Login);
                await session.SyncAsync(ct: ct);
                Report(true, "    server reachable, locks API working", $"{session.Head.Files.Count} files, {session.GetLocks().Count} checked out");
                if (session.Config.SolidworksVersion is { } required && sw != null)
                    Report(sw.Contains(required, StringComparison.OrdinalIgnoreCase), "    SOLIDWORKS version matches vault", $"vault requires {required}, this PC has {sw}");
                Report(string.Equals(Path.GetFullPath(session.Config.LocalRoot), Path.GetFullPath(session.LocalRoot), StringComparison.OrdinalIgnoreCase),
                    "    uses the vault's shared folder", $"vault expects {session.Config.LocalRoot}; this PC uses {session.LocalRoot}");
            }
            catch (VaultException ex)
            {
                Report(false, "    server", ex.Message);
            }
        }

        Console.WriteLine(_problems == 0 ? "All good." : $"{_problems} problem(s) found.");
    }

    private static void Report(bool ok, string check, string detail)
    {
        if (!ok) _problems++;
        Console.WriteLine($"  [{(ok ? " ok " : "FAIL")}] {check}: {detail}");
    }

    internal static string? SolidworksVersion()
    {
        var candidates = new List<string> { @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe" };
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
        {
            foreach (var name in key?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var setup = key!.OpenSubKey(name + @"\Setup");
                if (setup?.GetValue("SolidWorks Folder") is string folder) candidates.Add(Path.Combine(folder, "SLDWORKS.exe"));
            }
        }
        foreach (var exe in candidates.Where(File.Exists))
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return $"{info.ProductName} (build {info.FileVersion})";
        }
        return null;
    }
}
