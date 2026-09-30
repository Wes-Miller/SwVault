using System.Diagnostics;
using Microsoft.Win32;

namespace SwVault.Agent;

/// <summary>Tray icon: open vault folders, sync now, manage vaults, start at sign-in, logs, exit.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly AgentHost _agent;
    private readonly NotifyIcon _icon;
    private readonly Control _ui;

    public TrayContext(AgentHost agent)
    {
        _agent = agent;
        _ui = new Control();
        _ui.CreateControl(); // marshals toasts from background threads to the UI thread
        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "SwVault",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.DoubleClick += (_, _) => ShowVaults();
        _agent.Toast += (title, message) => _ui.BeginInvoke(() => _icon.ShowBalloonTip(8000, title, message, ToolTipIcon.Info));
        BuildMenu();

        // The add-in asks for these windows instead of having its own copies.
        _agent.WindowRequested += what => _ui.BeginInvoke(() =>
        {
            if (_agent.NeedsTeamSignIn) ShowSignIn();
            else if (what == "invite") ShowInvite();
            else if (what == "profile") ShowProfile();
            else if (what == "reviews") ShowReviews();
            else if (what.StartsWith("requestReview|", StringComparison.Ordinal)) ShowRequestReview(what["requestReview|".Length..]);
            else ShowVaults();
        });

        // Fresh team install: ask for the user name and password straight away.
        if (_agent.NeedsTeamSignIn) _ui.BeginInvoke(() => ShowSignIn());
    }

    private Form? _signIn;
    private Form? _invite;

    private void ShowSignIn()
    {
        if (_signIn != null)
        {
            _signIn.Activate();
            return;
        }
        bool signedIn;
        using (var form = _signIn = new SignInForm(_agent))
        {
            try
            {
                signedIn = form.ShowDialog() == DialogResult.OK;
            }
            finally
            {
                _signIn = null;
            }
        }
        if (signedIn) _ = AskForProfileIfMissingAsync();
    }

    /// <summary>After signing in with an existing account: ask "member or lead?" once if unknown.</summary>
    private async Task AskForProfileIfMissingAsync()
    {
        try
        {
            var (directory, me) = await Task.Run(() => _agent.TeamDirectoryAsync());
            var mine = directory.People.FirstOrDefault(p => string.Equals(p.Login, me, StringComparison.OrdinalIgnoreCase));
            if (mine != null && mine.Profile == null) ShowProfile(firstTime: true);
        }
        catch (Exception ex)
        {
            _agent.Log.Warn("Couldn't check the team profile: " + ex.Message);
        }
    }

    private Form? _reviews;

    private void ShowReviews()
    {
        if (_reviews != null)
        {
            _reviews.Activate();
            return;
        }
        var form = _reviews = new ReviewsForm(_agent);
        form.FormClosed += (_, _) => { _reviews = null; form.Dispose(); };
        form.Show(); // modeless: keep it open next to SOLIDWORKS
    }

    private void ShowRequestReview(string path)
    {
        using var form = new RequestReviewForm(_agent, path);
        form.ShowDialog();
    }

    private Form? _profile;

    private void ShowProfile(bool firstTime = false)
    {
        if (_profile != null)
        {
            _profile.Activate();
            return;
        }
        using var form = _profile = new ProfileForm(_agent, firstTime);
        try
        {
            form.ShowDialog();
        }
        finally
        {
            _profile = null;
        }
    }

    private void ShowInvite()
    {
        if (_invite != null)
        {
            _invite.Activate();
            return;
        }
        var target = _agent.InviteTarget();
        if (target == null)
        {
            MessageBox.Show("Connect to your team's vault first.", "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var form = _invite = new InviteForm(_agent, target.Value.VaultUrl, target.Value.Name);
        try
        {
            form.ShowDialog();
        }
        finally
        {
            _invite = null;
        }
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var title = menu.Items.Add($"SwVault ({_agent.Profile.Name})");
        title.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        if (_agent.NeedsTeamSignIn) menu.Items.Add($"Sign in to {_agent.Team!.Name}...", null, (_, _) => ShowSignIn());
        foreach (var registration in _agent.Vaults.Registrations)
        {
            var item = new ToolStripMenuItem($"Open {registration.Name ?? registration.Id} folder");
            var root = registration.LocalRoot;
            item.Click += (_, _) => { if (Directory.Exists(root)) Process.Start("explorer.exe", root); };
            menu.Items.Add(item);
        }
        if (!_agent.NeedsTeamSignIn && _agent.InviteTarget() != null)
        {
            var waiting = _agent.ReviewWatcher.WaitingForMe;
            menu.Items.Add(waiting > 0 ? $"Reviews ({waiting} waiting for you)..." : "Reviews...", null, (_, _) => ShowReviews());
            menu.Items.Add("My team role...", null, (_, _) => ShowProfile());
            menu.Items.Add("Invite people...", null, (_, _) => ShowInvite());
        }
        menu.Items.Add("Sync now", null, (_, _) => _agent.Sync.Poke());
        menu.Items.Add("Vaults...", null, (_, _) => ShowVaults());
        var startup = new ToolStripMenuItem("Start at sign-in") { Checked = StartsAtSignIn(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartAtSignIn(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add("Open log folder", null, (_, _) => Process.Start("explorer.exe", _agent.Log.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void ShowVaults()
    {
        if (_agent.NeedsTeamSignIn)
        {
            ShowSignIn();
            return;
        }
        using var form = new VaultsForm(_agent);
        form.ShowDialog();
    }

    private string RunValueName => _agent.Profile.Name == "default" ? "SwVaultAgent" : "SwVaultAgent." + _agent.Profile.Name;

    private bool StartsAtSignIn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) != null;
    }

    private void SetStartAtSignIn(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var args = _agent.Profile.Name == "default" ? "" : $" --profile {_agent.Profile.Name}";
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"{args}");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>A simple generated tray icon, so the repo carries no binary assets.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0, 102, 204));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "V", font, new Rectangle(0, 0, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
