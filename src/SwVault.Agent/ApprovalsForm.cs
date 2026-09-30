using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>Admins: approve or decline "I'm a subteam lead" and "I'm a responsible engineer" requests.</summary>
internal sealed class ApprovalsForm : Form
{
    private readonly AgentHost _agent;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Button _approve = new() { Text = "Approve", AutoSize = true, Enabled = false };
    private readonly Button _decline = new() { Text = "Decline", AutoSize = true, Enabled = false };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };

    public ApprovalsForm(AgentHost agent)
    {
        _agent = agent;
        Text = "SwVault - Approvals";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 720;
        Height = 420;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        _list.Columns.Add("Who", 170);
        _list.Columns.Add("Wants to be", 420);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { _approve, _decline, _refresh, _status });
        Controls.Add(_list);
        Controls.Add(buttons);

        _list.SelectedIndexChanged += (_, _) => _approve.Enabled = _decline.Enabled = _list.SelectedItems.Count > 0;
        _approve.Click += async (_, _) => await DecideAsync(true);
        _decline.Click += async (_, _) => await DecideAsync(false);
        _refresh.Click += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Loading...";
        try
        {
            var pending = await Task.Run(() => _agent.ApprovalsAsync());
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var l in pending.Leads)
                _list.Items.Add(new ListViewItem(new[] { Who(l.FullName, l.Login), $"{l.Subteam} subteam lead" }) { Tag = l });
            foreach (var e in pending.Engineers)
                _list.Items.Add(new ListViewItem(new[] { Who(e.FullName, e.Login), $"Responsible engineer of {e.Car} / {e.Subsystem}  [{e.Folder}]" }) { Tag = e });
            _list.EndUpdate();
            _status.Text = pending.Count == 0 ? "Nothing is waiting for approval." : $"{pending.Count} waiting.";
            _approve.Enabled = _decline.Enabled = false;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private static string Who(string fullName, string login) => string.IsNullOrWhiteSpace(fullName) || fullName == login ? login : $"{fullName} ({login})";

    private async Task DecideAsync(bool approve)
    {
        if (_list.SelectedItems.Count == 0) return;
        var tag = _list.SelectedItems[0].Tag;
        _status.Text = "Saving...";
        try
        {
            await Task.Run(() => tag switch
            {
                LeadRequest l => _agent.DecideLeadAsync(l.Login, approve),
                EngineerRequest e => _agent.DecideEngineerAsync(e.Login, e.SubsystemId, approve),
                _ => Task.CompletedTask,
            });
            await LoadAsync();
            _status.Text = (approve ? "Approved. " : "Declined. ") + _status.Text;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }
}
