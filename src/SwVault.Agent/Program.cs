using SwVault.Core.Client;

namespace SwVault.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var profileIndex = Array.IndexOf(args, "--profile");
        var profile = new SwVaultProfile(profileIndex >= 0 && profileIndex + 1 < args.Length ? args[profileIndex + 1] : null);

        // One agent per user and profile; a second launch (e.g. from the add-in) just exits.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SwVault.Agent." + profile.Name, out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        using var agent = new AgentHost(profile);
        Application.ThreadException += (_, e) => agent.Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => agent.Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            agent.Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        agent.Start();
        Application.Run(new TrayContext(agent));
    }
}
