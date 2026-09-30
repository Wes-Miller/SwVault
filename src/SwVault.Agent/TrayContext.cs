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
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var title = menu.Items.Add($"SwVault ({_agent.Profile.Name})");
        title.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        foreach (var registration in _agent.Vaults.Registrations)
        {
            var item = new ToolStripMenuItem($"Open {registration.Name ?? registration.Id} folder");
            var root = registration.LocalRoot;
            item.Click += (_, _) => { if (Directory.Exists(root)) Process.Start("explorer.exe", root); };
            menu.Items.Add(item);
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
