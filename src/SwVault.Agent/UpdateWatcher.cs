using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Team-wide updates: an admin publishes a new installer to the team server (scripts/package.ps1
/// -Publish); every agent notices within a few hours and offers it. Installing downloads the zip,
/// checks its SHA-256, waits until SOLIDWORKS is closed (the add-in DLL is loaded there) and runs
/// the package's install.ps1 -Quiet, which asks for administrator rights once and restarts the agent.
/// </summary>
internal sealed class UpdateWatcher : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private readonly AgentHost _agent;
    private readonly Action<string, string> _toast;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Version _current;
    private Version? _announced;
    private int _installing;

    /// <summary>A newer published installer, or null.</summary>
    public InstallerRelease? Available { get; private set; }

    /// <summary>What the update is doing right now ("Downloading 40%"...), or null.</summary>
    public string? Status { get; private set; }

    public event Action? Changed;

    public UpdateWatcher(AgentHost agent, Action<string, string> toast)
    {
        _agent = agent;
        _toast = toast;
        var v = typeof(UpdateWatcher).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _current = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>
    /// Only the installed agent of the default profile updates (not dev builds or simulated users).
    /// SWVAULT_UPDATES=1 turns it on anyway, for testing.
    /// </summary>
    public bool Enabled =>
        Environment.GetEnvironmentVariable("SWVAULT_UPDATES") == "1" ||
        (_agent.Profile.Name == "default" &&
         (Environment.ProcessPath ?? "").StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\", StringComparison.OrdinalIgnoreCase));

    public bool Installing => _installing != 0;

    public void Start()
    {
        AnnounceIfJustUpdated();
        if (Enabled) _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(FirstCheck, _cts.Token).ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                await CheckAsync(userAsked: false).ConfigureAwait(false);
                await Task.Delay(Interval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Checks the team server now. Returns a sentence for the user.</summary>
    public async Task<string> CheckAsync(bool userAsked)
    {
        var target = _agent.InviteTarget();
        if (target == null) return "Connect to your team's vault first.";
        await _checkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var release = await TeamInvites.LatestInstallerAsync(target.Value.VaultUrl, _cts.Token).ConfigureAwait(false);
            Available = release != null && release.IsNewerThan(_current) ? release : null;
            Changed?.Invoke();
            if (Available == null)
                return release == null ? "Your admin hasn't published an installer on the team server yet." : $"SwVault {_current.ToString(3)} is the newest version.";

            // Announce each version once per run, required ones at every check.
            if (!userAsked && (Available.Required || _announced != Available.Version))
            {
                _agent.Log.Info($"SwVault {Available.Version} is available{(Available.Required ? " (required)" : "")}; this is {_current.ToString(3)}");
                _announced = Available.Version;
                _toast(Available.Required ? $"SwVault {Available.Version} is required" : $"SwVault {Available.Version} is available",
                    (Available.Notes != null ? Available.Notes + "\n" : "") + "Click here or use the tray icon > Install update. It takes a minute; SOLIDWORKS must be closed.");
            }
            return $"SwVault {Available.Version} is available (you have {_current.ToString(3)}).";
        }
        catch (VaultException ex)
        {
            _agent.Log.Warn("Update check failed: " + ex.Message);
            return "Couldn't check for updates: " + ex.Message;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>Downloads, verifies and installs the available update. Returns false if one is already running.</summary>
    public bool BeginInstall()
    {
        var release = Available;
        var target = _agent.InviteTarget();
        if (release == null || target == null || Interlocked.Exchange(ref _installing, 1) == 1) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await InstallAsync(target.Value.VaultUrl, release).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _agent.Log.Error("Update failed", ex);
                _toast("SwVault update failed", ex is VaultException ? ex.Message : "See the log (tray icon > Open log folder).");
                SetStatus(null);
                Interlocked.Exchange(ref _installing, 0);
            }
        });
        return true;
    }

    private async Task InstallAsync(string vaultUrl, InstallerRelease release)
    {
        var folder = Path.Combine(_agent.Profile.BaseDir, "updates");
        var zip = Path.Combine(folder, $"SwVault-{release.Version}.zip");
        var package = Path.Combine(folder, $"SwVault-{release.Version}");
        _agent.Log.Info($"Updating to SwVault {release.Version}");

        SetStatus("Downloading update...");
        var progress = new Progress<double>(p => SetStatus($"Downloading update {p:P0}"));
        await TeamInvites.DownloadInstallerAsync(vaultUrl, release, zip, progress, _cts.Token).ConfigureAwait(false);

        SetStatus("Unpacking update...");
        if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        ZipFile.ExtractToDirectory(zip, package);
        var script = Path.Combine(package, "install.ps1");
        if (!File.Exists(script)) throw new VaultException(Protocol.ErrorCodes.Internal, "The update package has no install.ps1.");

        var asked = false;
        while (Process.GetProcessesByName("SLDWORKS").Length > 0)
        {
            if (!asked)
            {
                asked = true;
                SetStatus("Update ready: close SOLIDWORKS to install");
                _toast($"SwVault {release.Version} is ready", "Save your work and close SOLIDWORKS; the update installs as soon as it's closed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
        }

        SetStatus("Installing update...");
        _agent.Log.Info("Running " + script);
        // The installer stops this agent, asks for administrator rights once, and starts the new agent.
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Quiet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = package,
        });
    }

    private void SetStatus(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>"SwVault updated to 0.3.0" after the installer restarted a newer agent.</summary>
    private void AnnounceIfJustUpdated()
    {
        if (_agent.Profile.Name != "default") return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\SwVault");
            var previous = key.GetValue("LastRunVersion") as string;
            var now = _current.ToString(3);
            if (previous == now) return;
            key.SetValue("LastRunVersion", now);
            if (previous != null && Version.TryParse(previous, out var old) && old < _current)
                _toast($"SwVault updated to {now}", "You can open SOLIDWORKS again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _agent.Log.Warn("Couldn't record the SwVault version: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
