using System.Text.Json;
using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Every two minutes, checks review requests: tells leads about new requests for them and tells
/// members when a lead approves or asks for changes. Remembers what it has already announced in
/// %LOCALAPPDATA%\SwVault\reviews-seen.json.
/// </summary>
internal sealed class ReviewWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private readonly AgentHost _agent;
    private readonly Action<string, string> _toast;
    private readonly string _stateFile;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, string> _seen = new();

    /// <summary>Open requests waiting for me as a lead (for the tray menu).</summary>
    public int WaitingForMe { get; private set; }

    /// <summary>Raised when WaitingForMe or my requests change, so open windows can refresh.</summary>
    public event Action? Changed;

    public ReviewWatcher(AgentHost agent, Action<string, string> toast)
    {
        _agent = agent;
        _toast = toast;
        _stateFile = Path.Combine(agent.Profile.BaseDir, "reviews-seen.json");
        try
        {
            if (File.Exists(_stateFile)) _seen = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(_stateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _agent.Log.Warn("Couldn't read reviews-seen.json: " + ex.Message);
        }
    }

    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>Check now (after the user acted on a review).</summary>
    public Task CheckNowAsync() => CheckAsync(announce: true);

    private async Task RunAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(announce: true, firstRun: first).ConfigureAwait(false);
                first = false;
            }
            catch (Exception ex)
            {
                _agent.Log.Warn("Review check failed: " + ex.Message);
            }
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

    private async Task CheckAsync(bool announce, bool firstRun = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CheckCoreAsync(announce, firstRun).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckCoreAsync(bool announce, bool firstRun)
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

        var forMe = await Reviews.ListAsync(target.Value.VaultUrl, me, assignedToMe: true, includeClosed: false, _cts.Token).ConfigureAwait(false);
        var mine = await Reviews.ListAsync(target.Value.VaultUrl, me, assignedToMe: false, includeClosed: true, _cts.Token).ConfigureAwait(false);
        var waiting = forMe.Where(r => r.Status == ReviewStatus.Waiting).ToList();

        var messages = new List<string>();
        foreach (var r in waiting.Where(r => !_seen.ContainsKey(r.Number)))
            messages.Add($"{r.Requester} asked you for a {r.KindText.ToLowerInvariant()} of {r.FileName}.");
        foreach (var r in mine)
        {
            if (!_seen.TryGetValue(r.Number, out var before) || before == r.Status.ToString()) continue;
            if (r.Status == ReviewStatus.Approved) messages.Add($"{r.Lead} approved your {r.KindText.ToLowerInvariant()} of {r.FileName}.");
            else if (r.Status == ReviewStatus.ChangesRequested) messages.Add($"{r.Lead} asked for changes on {r.FileName} ({r.KindText.ToLowerInvariant()}).");
        }

        var changed = WaitingForMe != waiting.Count || messages.Count > 0;
        WaitingForMe = waiting.Count;
        foreach (var r in forMe.Concat(mine)) _seen[r.Number] = r.Status.ToString();
        Save();

        if (announce && messages.Count > 0)
        {
            if (firstRun && messages.Count > 2)
                _toast("SwVault reviews", $"{waiting.Count} review request(s) are waiting for you. Tray icon > Reviews.");
            else
                _toast("SwVault reviews", string.Join("\n", messages.Take(4)) + (messages.Count > 4 ? $"\n...and {messages.Count - 4} more" : "") + "\nTray icon > Reviews.");
        }
        if (changed) Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_seen));
        }
        catch (IOException ex)
        {
            _agent.Log.Warn("Couldn't save reviews-seen.json: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
