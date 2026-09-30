using Microsoft.Win32;
using SwVault.Core;
using SwVault.Core.Client;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>Everything the agent runs, independent of the tray UI (so tests can host it headless).</summary>
internal sealed class AgentHost : IDisposable
{
    public SwVaultProfile Profile { get; }
    public VaultManager Vaults { get; }
    public FileLog Log { get; }
    public PipeServer Server { get; }
    public SyncLoop Sync { get; }
    public JobManager Jobs { get; }

    /// <summary>The team vault this install was packaged for (team.json), or null.</summary>
    public TeamConfig? Team { get; }

    /// <summary>True until this PC is connected to a vault while a team.json says which one to join.</summary>
    public bool NeedsTeamSignIn => Team != null && Vaults.Registrations.Count == 0;

    /// <summary>Raised for toast-worthy events; the tray shows them as Windows notifications.</summary>
    public event Action<string, string>? Toast;

    public AgentHost(SwVaultProfile profile)
    {
        Profile = profile;
        Log = new FileLog(profile.LogDir);
        Vaults = new VaultManager(profile);
        Jobs = new JobManager(Vaults, Broadcast, Log);
        try
        {
            Team = TeamConfig.Load();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read team.json: " + ex.Message);
        }
        var dispatcher = new RpcDispatcher(Vaults, Jobs, Log, () => Sync?.Poke(), Team, JoinTeamAsync);
        Server = new PipeServer(profile.PipeName, dispatcher.DispatchAsync, Log);
        Sync = new SyncLoop(Vaults, Broadcast, RaiseToast, Log);
    }

    public void Start()
    {
        Log.Info($"SwVault agent {typeof(AgentHost).Assembly.GetName().Version} starting (profile {Profile.Name}, pipe {Profile.PipeName})");
        RegisterLocation();
        Server.Start();
        Sync.Start();
        _ = Task.Run(ResumePendingAsync);
    }

    /// <summary>
    /// First sign-in on a team install: connect with user name and password, then download the
    /// vault in the background so the files are there when SOLIDWORKS opens.
    /// </summary>
    public async Task<VaultSession> JoinTeamAsync(string userName, string password)
    {
        var team = Team ?? throw VaultException.NotFound("This SwVault install has no team.json; connect with Vaults... instead.");
        var session = await TeamJoin.JoinAsync(Vaults, team, userName, password).ConfigureAwait(false);
        Log.Info($"Joined {team.Name} as {session.User?.Login}");
        Broadcast(Notifications.StatusChanged, new StatusChangedNotification { VaultId = session.VaultId, FullRefresh = true });
        Sync.Poke();
        if (team.DownloadAllOnJoin) _ = Task.Run(() => DownloadAllAsync(session));
        return session;
    }

    private async Task DownloadAllAsync(VaultSession session)
    {
        RaiseToast("SwVault", $"Signed in. Downloading the {session.Config.Name} files to {session.LocalRoot}...");
        var job = await Jobs.StartAsync(new JobRequest { Kind = JobKind.GetLatest, VaultId = session.VaultId, AutoApply = true }).ConfigureAwait(false);
        if (job.State == JobState.Completed)
            RaiseToast("SwVault", $"All {session.Config.Name} files are in {session.LocalRoot}. Open them from SOLIDWORKS.");
        else
            RaiseToast("SwVault", "Downloading the vault didn't finish: " + (job.Error ?? job.State.ToString()) + " Use Get Latest in SOLIDWORKS to retry.");
    }

    private void Broadcast(string method, object payload) => Server.Broadcast(method, payload);

    private void RaiseToast(string title, string message)
    {
        Toast?.Invoke(title, message);
        Broadcast(Notifications.Toast, new ToastNotification { Title = title, Message = message });
    }

    /// <summary>Lets the add-in find and start the agent (HKCU\Software\SwVault\AgentPath).</summary>
    private void RegisterLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\SwVault");
            key.SetValue(Profile.Name == "default" ? "AgentPath" : "AgentPath." + Profile.Name, Environment.ProcessPath ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not record the agent location: " + ex.Message);
        }
    }

    private async Task ResumePendingAsync()
    {
        foreach (var registration in Vaults.Registrations)
        {
            try
            {
                var session = await Vaults.GetAsync(registration.Id).ConfigureAwait(false);
                await session.ResumePendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not finish pending work for {registration.Id}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Log.Info("SwVault agent stopping");
        Sync.Dispose();
        Server.Dispose();
        Vaults.Dispose();
    }
}
