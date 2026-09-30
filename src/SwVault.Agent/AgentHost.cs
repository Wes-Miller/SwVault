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
    public ReviewWatcher ReviewWatcher { get; }

    /// <summary>The team vault this install was packaged for (team.json), or null.</summary>
    public TeamConfig? Team { get; }

    /// <summary>True until this PC is connected to a vault while a team.json says which one to join.</summary>
    public bool NeedsTeamSignIn => Team != null && Vaults.Registrations.Count == 0;

    /// <summary>Raised for toast-worthy events; the tray shows them as Windows notifications.</summary>
    public event Action<string, string>? Toast;

    /// <summary>A client (the add-in) asked for one of the agent's windows: "signIn" or "invite".</summary>
    public event Action<string>? WindowRequested;

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
        var dispatcher = new RpcDispatcher(Vaults, Jobs, Log, () => Sync?.Poke(), Team, JoinTeamAsync, what => WindowRequested?.Invoke(what));
        Server = new PipeServer(profile.PipeName, dispatcher.DispatchAsync, Log);
        Sync = new SyncLoop(Vaults, Broadcast, RaiseToast, Log);
        ReviewWatcher = new ReviewWatcher(this, RaiseToast);
    }

    public void Start()
    {
        Log.Info($"SwVault agent {typeof(AgentHost).Assembly.GetName().Version} starting (profile {Profile.Name}, pipe {Profile.PipeName})");
        RegisterLocation();
        Server.Start();
        Sync.Start();
        ReviewWatcher.Start();
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

    /// <summary>New member with an invite code: create the account, then sign in as above.</summary>
    public async Task<VaultSession> JoinWithInviteAsync(string code, string userName, string password, string? fullName,
        string? email, string? emailCode, TeamProfile profile)
    {
        var team = Team ?? throw VaultException.NotFound("This SwVault install has no team.json, so it doesn't know which vault the invite is for.");
        try
        {
            await TeamInvites.RedeemAsync(team.VaultUrl, code, userName, password, fullName, email, emailCode, profile).ConfigureAwait(false);
            Log.Info($"Redeemed invite {code} as {userName}");
        }
        catch (VaultException ex) when (ex.Code is ErrorCodes.Conflict or ErrorCodes.NotFound)
        {
            // "Name taken" or "code used up" can be this same person retrying after the account was
            // created but signing in failed (e.g. the connection dropped). If the password works, carry on.
            try
            {
                var session = await JoinTeamAsync(userName, password).ConfigureAwait(false);
                ClearPendingInvite();
                return session;
            }
            catch (VaultException retry) when (retry.Code == ErrorCodes.Unauthorized)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex); // the redeem error is the useful one
            }
        }
        ClearPendingInvite();
        return await JoinTeamAsync(userName, password).ConfigureAwait(false);
    }

    /// <summary>
    /// Invite code that came with this install: from the installer's folder name (the download link
    /// names the zip after the code; install.ps1 saves it) or baked into team.json.
    /// </summary>
    public string? PendingInviteCode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SwVault");
            var saved = TeamInvites.NormalizeCode(key?.GetValue("InviteCode") as string);
            if (saved != null) return saved;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the saved invite code: " + ex.Message);
        }
        return TeamInvites.NormalizeCode(Team?.InviteCode);
    }

    private void ClearPendingInvite()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SwVault", writable: true);
            key?.DeleteValue("InviteCode", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear the saved invite code: " + ex.Message);
        }
    }

    /// <summary>The vault invites are for (the team's, else the first connected one), and its display name.</summary>
    public (string VaultUrl, string Name)? InviteTarget()
    {
        if (Team != null) return (Team.VaultUrl, Team.Name);
        var first = Vaults.Registrations.FirstOrDefault();
        return first == null ? null : (first.RemoteUrl, first.Name ?? first.Id);
    }

    /// <summary>This PC's saved sign-in for the vault server (the team service checks who it is).</summary>
    public async Task<Core.Auth.Credential> MyCredentialAsync(string vaultUrl)
    {
        var credential = await Profile.CredentialProvider.GetAsync(new Uri(vaultUrl), interactive: false, CancellationToken.None).ConfigureAwait(false);
        return credential ?? throw VaultException.Unauthorized("Sign in to the vault on this PC first.");
    }

    /// <summary>The team directory and this PC's user in it (for profiles and review requests).</summary>
    public async Task<(TeamDirectory Directory, string Me)> TeamDirectoryAsync()
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        var me = await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false);
        var directory = await TeamInvites.PeopleAsync(target.VaultUrl, me).ConfigureAwait(false);
        return (directory, me.UserName);
    }

    public async Task SaveMyProfileAsync(TeamProfile profile)
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        await TeamInvites.SaveMyProfileAsync(target.VaultUrl, await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false), profile).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- review requests

    private async Task<(string VaultUrl, Core.Auth.Credential Me)> ReviewContextAsync()
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        return (target.VaultUrl, await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false));
    }

    /// <summary>The vault path and checked-in version a review of this local file refers to.</summary>
    public async Task<(string VaultPath, int Version, bool ModifiedLocally)> ReviewSubjectAsync(string localPath)
    {
        var session = await Vaults.FindByLocalPathAsync(localPath).ConfigureAwait(false)
            ?? throw VaultException.BadRequest(Path.GetFileName(localPath) + " isn't in a vault folder.");
        var status = await session.GetStatusAsync(localPath).ConfigureAwait(false);
        if (status == null || status.ServerVersion == 0)
            throw VaultException.BadRequest(Path.GetFileName(localPath) + " isn't in the vault yet. Add it (Add to Vault) or check it in first, so the lead can open it.");
        var modified = status.LocalState is LocalState.Modified or LocalState.Conflict;
        return (status.Path, status.ServerVersion, modified);
    }

    public async Task<ReviewRequest> RequestReviewAsync(string localPath, ReviewKind kind, string lead, string? message)
    {
        var (vaultPath, version, _) = await ReviewSubjectAsync(localPath).ConfigureAwait(false);
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        var review = await Reviews.RequestAsync(vaultUrl, me, kind, lead, vaultPath, version, message).ConfigureAwait(false);
        Log.Info($"Review #{review.Number} ({kind}) of {vaultPath} v{version} requested from {lead}");
        _ = EmailAboutReviewAsync(vaultUrl, me, review.Number);
        _ = ReviewWatcher.CheckNowAsync();
        return review;
    }

    public async Task<IReadOnlyList<ReviewRequest>> ListReviewsAsync(bool forMe, bool includeClosed)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.ListAsync(vaultUrl, me, forMe, includeClosed).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReviewComment>> ReviewCommentsAsync(ReviewRequest review)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.CommentsAsync(vaultUrl, me, review.Number).ConfigureAwait(false);
    }

    public async Task RespondToReviewAsync(ReviewRequest review, ReviewDecision decision, string? comment)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await Reviews.RespondAsync(vaultUrl, me, review, decision, comment).ConfigureAwait(false);
        Log.Info($"Review #{review.Number}: {decision}");
        if (decision is ReviewDecision.Approve or ReviewDecision.RequestChanges) _ = EmailAboutReviewAsync(vaultUrl, me, review.Number);
        _ = ReviewWatcher.CheckNowAsync();
    }

    /// <summary>Best effort: the review itself is already saved; email is a courtesy.</summary>
    private async Task EmailAboutReviewAsync(string vaultUrl, Core.Auth.Credential me, int number)
    {
        try
        {
            await TeamInvites.NotifyReviewAsync(vaultUrl, me, number).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't email about review #{number}: {ex.Message}");
        }
    }

    /// <summary>Local path of a vault file in the vault this PC is connected to (for "Open file").</summary>
    public string? LocalPathOf(string vaultPath)
    {
        var target = InviteTarget();
        var registration = Vaults.Registrations.FirstOrDefault(r => target != null && string.Equals(r.RemoteUrl, target.Value.VaultUrl, StringComparison.OrdinalIgnoreCase))
            ?? Vaults.Registrations.FirstOrDefault();
        return registration == null ? null : Path.Combine(registration.LocalRoot, vaultPath.Replace('/', Path.DirectorySeparatorChar));
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
        ReviewWatcher.Dispose();
        Sync.Dispose();
        Server.Dispose();
        Vaults.Dispose();
    }
}
