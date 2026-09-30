using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Every two minutes, reads the team's cars and subsystems (so check-ins in my subsystems can be
/// announced) and, for admins, lead / responsible-engineer requests waiting for approval. Also tells
/// me when my own requests are approved.
/// </summary>
internal sealed class TeamWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private readonly AgentHost _agent;
    private readonly Action<string, string> _toast;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _myPending = new(StringComparer.OrdinalIgnoreCase);
    private bool _firstRun = true;
    private bool _serviceMissingLogged;

    public IReadOnlyList<Car> Cars { get; private set; } = Array.Empty<Car>();
    public string? Me { get; private set; }

    /// <summary>True when the team service accepted me as an admin (Owners).</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>Requests waiting for an admin (0 unless I'm an admin).</summary>
    public int PendingApprovals { get; private set; }

    public event Action? Changed;

    public TeamWatcher(AgentHost agent, Action<string, string> toast)
    {
        _agent = agent;
        _toast = toast;
    }

    public void Start() => _ = Task.Run(RunAsync);

    public Task CheckNowAsync() => CheckAsync();

    /// <summary>Subsystems of this vault where I'm an approved responsible engineer.</summary>
    public IReadOnlyList<Subsystem> SubsystemsIEngineer(VaultSession session)
    {
        var target = _agent.InviteTarget();
        if (Me == null || target == null || !string.Equals(target.Value.VaultUrl, session.RemoteUrl, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<Subsystem>();
        return Cars.SelectMany(c => c.Subsystems).Where(s => s.IsEngineer(Me)).ToList();
    }

    /// <summary>The subsystem a vault path belongs to, from the last check.</summary>
    public Subsystem? SubsystemFor(string vaultPath) => TeamInvites.SubsystemFor(Cars, vaultPath);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await CheckAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(Interval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CheckCoreAsync().ConfigureAwait(false);
        }
        catch (VaultException ex) when (ex.Code == Protocol.ErrorCodes.NotFound && ex.Message.Contains("doesn't have invites", StringComparison.Ordinal))
        {
            if (!_serviceMissingLogged) _agent.Log.Info("This vault server has no team service, so there are no subsystems or approvals.");
            _serviceMissingLogged = true;
        }
        catch (Exception ex)
        {
            _agent.Log.Warn("Team check failed: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckCoreAsync()
    {
        var target = _agent.InviteTarget();
        if (target == null || _agent.NeedsTeamSignIn) return;
        Core.Auth.Credential me;
        try
        {
            me = await _agent.MyCredentialAsync(target.Value.VaultUrl).ConfigureAwait(false);
        }
        catch (VaultException)
        {
            return; // not signed in yet
        }
        var url = target.Value.VaultUrl;
        var cars = await TeamInvites.CarsAsync(url, me, _cts.Token).ConfigureAwait(false);
        var directory = await TeamInvites.PeopleAsync(url, me, _cts.Token).ConfigureAwait(false);
        Me = me.UserName;
        Cars = cars;

        var messages = new List<string>();
        // My own requests that an admin has approved since the last check.
        var myPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mePerson = directory.People.FirstOrDefault(p => string.Equals(p.Login, me.UserName, StringComparison.OrdinalIgnoreCase));
        if (mePerson?.PendingLead != null) myPending.Add("lead:" + mePerson.PendingLead);
        foreach (var s in cars.SelectMany(c => c.Subsystems).Where(s => s.HasPendingRequest(me.UserName))) myPending.Add("re:" + s.Id);
        foreach (var was in _myPending.Where(p => !myPending.Contains(p)))
        {
            if (was.StartsWith("lead:", StringComparison.Ordinal) && mePerson?.Profile?.IsLead == true)
                messages.Add($"You're now the {mePerson.Profile.Subteam} lead.");
            else if (was.StartsWith("re:", StringComparison.Ordinal)
                     && cars.SelectMany(c => c.Subsystems).FirstOrDefault(s => s.Id == was[3..]) is { } s && s.IsEngineer(me.UserName))
                messages.Add($"You're now a responsible engineer of {s.CarName} / {s.Name}.");
        }
        _myPending = myPending;

        var wasAdmin = IsAdmin;
        var pendingBefore = PendingApprovals;
        try
        {
            var approvals = await TeamInvites.ApprovalsAsync(url, me, _cts.Token).ConfigureAwait(false);
            IsAdmin = true;
            PendingApprovals = approvals.Count;
            var keys = approvals.Leads.Select(l => ($"lead:{l.Login}:{l.Subteam}", $"{Name(l.FullName, l.Login)} wants to be the {l.Subteam} lead."))
                .Concat(approvals.Engineers.Select(e => ($"re:{e.Login}:{e.SubsystemId}", $"{Name(e.FullName, e.Login)} wants to be responsible engineer of {e.Car} / {e.Subsystem}.")));
            foreach (var (key, text) in keys)
                if (_announced.Add(key) && !_firstRun) messages.Add(text);
            if (_firstRun && approvals.Count > 0) messages.Add($"{approvals.Count} request(s) are waiting for your approval.");
        }
        catch (VaultException ex) when (ex.Code == Protocol.ErrorCodes.Forbidden)
        {
            IsAdmin = false;
            PendingApprovals = 0;
        }
        _firstRun = false;

        if (messages.Count > 0)
            _toast("SwVault team", string.Join("\n", messages.Take(4)) + (IsAdmin && PendingApprovals > 0 ? "\nTray icon > Approvals." : ""));
        if (wasAdmin != IsAdmin || pendingBefore != PendingApprovals || messages.Count > 0) Changed?.Invoke();
    }

    private static string Name(string fullName, string login) => string.IsNullOrWhiteSpace(fullName) ? login : fullName;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
