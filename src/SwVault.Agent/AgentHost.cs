using Microsoft.Win32;
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

    /// <summary>Raised for toast-worthy events; the tray shows them as Windows notifications.</summary>
    public event Action<string, string>? Toast;

    public AgentHost(SwVaultProfile profile)
    {
        Profile = profile;
        Log = new FileLog(profile.LogDir);
        Vaults = new VaultManager(profile);
        Jobs = new JobManager(Vaults, Broadcast, Log);
        var dispatcher = new RpcDispatcher(Vaults, Jobs, Log, () => Sync?.Poke());
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
