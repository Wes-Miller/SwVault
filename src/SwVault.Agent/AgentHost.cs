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
    public TeamWatcher TeamWatcher { get; }
    public UpdateWatcher Updates { get; }

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
        var dispatcher = new RpcDispatcher(Vaults, Jobs, Log, () => Sync?.Poke(), Team, JoinTeamAsync, what => WindowRequested?.Invoke(what), SubsystemInfoAsync);
        Server = new PipeServer(profile.PipeName, dispatcher.DispatchAsync, Log);
        TeamWatcher = new TeamWatcher(this, RaiseToast);
        Sync = new SyncLoop(Vaults, Broadcast, RaiseToast, Log, TeamWatcher.SubsystemsIEngineer);
        ReviewWatcher = new ReviewWatcher(this, RaiseToast);
        Updates = new UpdateWatcher(this, RaiseToast);
    }

    public void Start()
    {
        Log.Info($"SwVault agent {typeof(AgentHost).Assembly.GetName().Version} starting (profile {Profile.Name}, pipe {Profile.PipeName})");
        RegisterLocation();
        Server.Start();
        Sync.Start();
        ReviewWatcher.Start();
        TeamWatcher.Start();
        Updates.Start();
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

    /// <summary>Saves my role; returns the subteam when becoming its lead waits for an admin.</summary>
    public async Task<string?> SaveMyProfileAsync(TeamProfile profile)
    {
        var target = InviteTarget() ?? throw VaultException.NotFound("Connect to your team's vault first.");
        var pending = await TeamInvites.SaveMyProfileAsync(target.VaultUrl, await MyCredentialAsync(target.VaultUrl).ConfigureAwait(false), profile).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
        return pending;
    }

    // ---------------------------------------------------------------- cars, subsystems, approvals

    public async Task<(IReadOnlyList<Car> Cars, string Me)> CarsAsync()
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return (await TeamInvites.CarsAsync(vaultUrl, me).ConfigureAwait(false), me.UserName);
    }

    public async Task AddCarAsync(string name, string? folder)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.AddCarAsync(vaultUrl, me, name, folder).ConfigureAwait(false);
        Log.Info($"Added car {name}");
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>Adds a subsystem and creates its folder in the vault folder on this PC.</summary>
    public async Task AddSubsystemAsync(string carId, string name, string? folder)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.AddSubsystemAsync(vaultUrl, me, carId, name, folder).ConfigureAwait(false);
        Log.Info($"Added subsystem {name}");
        var (cars, _) = await CarsAsync().ConfigureAwait(false);
        var added = cars.FirstOrDefault(c => c.Id == carId)?.Subsystems.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        var local = added == null ? null : LocalPathOf(added.Folder);
        if (local != null) Directory.CreateDirectory(local);
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>Asks to be a responsible engineer; returns true when approved at once (admins).</summary>
    public async Task<bool> ClaimEngineerAsync(string subsystemId)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        var approved = await TeamInvites.ClaimEngineerAsync(vaultUrl, me, subsystemId).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
        return approved;
    }

    public async Task RemoveEngineerAsync(string subsystemId, string login)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.RemoveEngineerAsync(vaultUrl, me, subsystemId, login).ConfigureAwait(false);
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task<PendingApprovals> ApprovalsAsync()
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await TeamInvites.ApprovalsAsync(vaultUrl, me).ConfigureAwait(false);
    }

    public async Task DecideLeadAsync(string login, bool approve)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.DecideLeadAsync(vaultUrl, me, login, approve).ConfigureAwait(false);
        Log.Info($"{(approve ? "Approved" : "Declined")} {login} as lead");
        _ = TeamWatcher.CheckNowAsync();
    }

    public async Task DecideEngineerAsync(string login, string subsystemId, bool approve)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        await TeamInvites.DecideEngineerAsync(vaultUrl, me, login, subsystemId, approve).ConfigureAwait(false);
        Log.Info($"{(approve ? "Approved" : "Declined")} {login} as responsible engineer of {subsystemId}");
        _ = TeamWatcher.CheckNowAsync();
    }

    /// <summary>For the add-in's task pane: the subsystem a local file belongs to, from the last team check.</summary>
    public async Task<SubsystemInfo?> SubsystemInfoAsync(string localPath)
    {
        var session = await Vaults.FindByLocalPathAsync(localPath).ConfigureAwait(false);
        var vaultPath = session == null ? null : Core.Util.PathRules.ToVaultPath(session.LocalRoot, localPath);
        var subsystem = vaultPath == null ? null : TeamWatcher.SubsystemFor(vaultPath);
        if (subsystem == null) return null;
        return new SubsystemInfo
        {
            Car = subsystem.CarName,
            Name = subsystem.Name,
            Folder = subsystem.Folder,
            Engineers = subsystem.Engineers.Where(e => e.Approved).Select(e => e.Login).ToArray(),
            PendingEngineers = subsystem.Engineers.Where(e => !e.Approved).Select(e => e.Login).ToArray(),
        };
    }

    /// <summary>
    /// Responsible engineers to cc on my review request: only for general members' requests, only
    /// approved engineers of the file's subsystem, never me or the lead I'm asking.
    /// </summary>
    internal static (IReadOnlyList<string> Cc, string? Reason) ReviewCc(TeamDirectory directory, IReadOnlyList<Car> cars, string me, string lead, string vaultPath)
    {
        var myProfile = directory.People.FirstOrDefault(p => string.Equals(p.Login, me, StringComparison.OrdinalIgnoreCase))?.Profile;
        if (myProfile?.IsLead == true) return (Array.Empty<string>(), null);
        var subsystem = TeamInvites.SubsystemFor(cars, vaultPath);
        if (subsystem == null) return (Array.Empty<string>(), null);
        var cc = subsystem.ApprovedEngineers
            .Where(e => !string.Equals(e, me, StringComparison.OrdinalIgnoreCase) && !string.Equals(e, lead, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (cc, cc.Count == 0 ? null : $"responsible engineers of {subsystem.CarName} / {subsystem.Name}");
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
        IReadOnlyList<string> cc = Array.Empty<string>();
        string? ccReason = null;
        try
        {
            var directory = await TeamInvites.PeopleAsync(vaultUrl, me).ConfigureAwait(false);
            var cars = await TeamInvites.CarsAsync(vaultUrl, me).ConfigureAwait(false);
            (cc, ccReason) = ReviewCc(directory, cars, me.UserName, lead, vaultPath);
        }
        catch (VaultException ex)
        {
            Log.Warn("Couldn't work out who to cc on the review: " + ex.Message); // the request itself still goes out
        }
        var review = await Reviews.RequestAsync(vaultUrl, me, kind, lead, vaultPath, version, message, cc, ccReason).ConfigureAwait(false);
        Log.Info($"Review #{review.Number} ({kind}) of {vaultPath} v{version} requested from {lead}" + (cc.Count > 0 ? ", cc " + string.Join(", ", cc) : ""));
        _ = EmailAboutReviewAsync(vaultUrl, me, review.Number);
        _ = ReviewWatcher.CheckNowAsync();
        return review;
    }

    public async Task<IReadOnlyList<ReviewRequest>> ListReviewsAsync(bool forMe, bool includeClosed)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.ListAsync(vaultUrl, me, forMe, includeClosed).ConfigureAwait(false);
    }

    /// <summary>Reviews I'm cc'd on as a responsible engineer.</summary>
    public async Task<IReadOnlyList<ReviewRequest>> ListCcReviewsAsync(bool includeClosed)
    {
        var (vaultUrl, me) = await ReviewContextAsync().ConfigureAwait(false);
        return await Reviews.ListCcAsync(vaultUrl, me, includeClosed).ConfigureAwait(false);
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
        TeamWatcher.Dispose();
        Updates.Dispose();
        Sync.Dispose();
        Server.Dispose();
        Vaults.Dispose();
    }
}
