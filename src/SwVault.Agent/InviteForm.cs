using SwVault.Core;
using SwVault.Core.Auth;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Vault admins invite people: pick access, how many people and for how long, then Create. The
/// ready-to-paste message (download link + code) is copied to the clipboard for a team chat or email.
/// </summary>
internal sealed class InviteForm : Form
{
    private readonly AgentHost _agent;
    private readonly string _vaultUrl;
    private readonly string _teamName;
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NumericUpDown _uses = new() { Minimum = 1, Maximum = 500, Value = 10, Width = 70 };
    private readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 90, Value = 14, Width = 70 };
    private readonly TextBox _note = new() { Width = 240, PlaceholderText = "(optional) e.g. 2027 season" };
    private readonly Button _create = new() { Text = "Create invite", AutoSize = true };
    private readonly TextBox _message = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 150 };
    private readonly Button _copy = new() { Text = "Copy message", AutoSize = true, Enabled = false };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, Height = 120, MultiSelect = false };
    private readonly Button _revoke = new() { Text = "Revoke", AutoSize = true, Enabled = false };
    private readonly Button _copySelected = new() { Text = "Copy message", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0) };
    private Credential? _admin;

    public InviteForm(AgentHost agent, string vaultUrl, string teamName)
    {
        _agent = agent;
        _vaultUrl = vaultUrl;
        _teamName = teamName;
        Text = "SwVault - Invite people to " + teamName;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 620;
        Height = 640;
        MinimumSize = new Size(560, 560);
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        _role.Items.AddRange(new object[] { "Designer (check out and check in)", "Viewer (open and download only)" });
        _role.SelectedIndex = 0;
        _list.Columns.Add("Code", 130);
        _list.Columns.Add("Access", 80);
        _list.Columns.Add("Left", 60);
        _list.Columns.Add("Expires", 90);
        _list.Columns.Add("Note", 200);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        void Row(string label, Control control)
        {
            form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 0) });
            form.Controls.Add(control);
        }
        Row("Access", _role);
        Row("How many people", _uses);
        Row("Expires after (days)", _days);
        Row("Note", _note);
        form.Controls.Add(new Label());
        form.Controls.Add(_create);
        root.Controls.Add(form);

        var messageBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        messageBar.Controls.Add(new Label { Text = "Message to send (paste it into your team chat or an email):", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        messageBar.Controls.Add(_copy);
        root.Controls.Add(messageBar);
        root.Controls.Add(_message);

        root.Controls.Add(new Label { Text = "Active invites", AutoSize = true, Margin = new Padding(0, 12, 0, 4), Font = new Font(Font, FontStyle.Bold) });
        var listBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        listBar.Controls.Add(_copySelected);
        listBar.Controls.Add(_revoke);
        root.Controls.Add(listBar);
        root.Controls.Add(_list);
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0),
            Text = "People who use an invite choose their own user name and password. Remove someone later on the server: swvault-admin.sh disable-user <name>.",
        });
        root.Controls.Add(_status);
        Controls.Add(root);

        _create.Click += async (_, _) => await CreateAsync();
        _copy.Click += (_, _) => Copy(_message.Text);
        _list.SelectedIndexChanged += (_, _) => _revoke.Enabled = _copySelected.Enabled = _list.SelectedItems.Count > 0;
        _revoke.Click += async (_, _) => await RevokeAsync();
        _copySelected.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is InviteInfo invite) Copy(TeamInvites.Message(_teamName, _vaultUrl, invite));
        };
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task<Credential> AdminAsync() => _admin ??= await _agent.MyCredentialAsync(_vaultUrl);

    private async Task LoadAsync()
    {
        try
        {
            var admin = await AdminAsync();
            var invites = await Task.Run(() => TeamInvites.ListAsync(_vaultUrl, admin));
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var i in invites.OrderBy(i => i.Expires))
                _list.Items.Add(new ListViewItem(new[] { i.Code, i.Role, $"{i.UsesLeft}/{i.Uses}", i.Expires.ToLocalTime().ToString("MMM d"), i.Note ?? "" }) { Tag = i });
            _list.EndUpdate();
        }
        catch (VaultException ex)
        {
            ShowError(ex);
        }
    }

    private async Task CreateAsync()
    {
        _create.Enabled = false;
        _status.Text = "Creating...";
        try
        {
            var admin = await AdminAsync();
            var role = _role.SelectedIndex == 1 ? "viewer" : "designer";
            var invite = await Task.Run(() => TeamInvites.CreateAsync(_vaultUrl, admin, role, (int)_uses.Value, (int)_days.Value, _note.Text.Trim()));
            _message.Text = TeamInvites.Message(_teamName, _vaultUrl, invite).Replace("\n", "\r\n");
            _copy.Enabled = true;
            Copy(_message.Text);
            await LoadAsync();
        }
        catch (VaultException ex)
        {
            ShowError(ex);
        }
        finally
        {
            _create.Enabled = true;
        }
    }

    private async Task RevokeAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not InviteInfo invite) return;
        if (MessageBox.Show(this, $"Stop invite {invite.Code} from working? People who already joined keep their accounts.", "SwVault",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            await Task.Run(() => TeamInvites.RevokeAsync(_vaultUrl, _admin!, invite.Code));
            _status.Text = $"Invite {invite.Code} no longer works.";
            await LoadAsync();
        }
        catch (VaultException ex)
        {
            ShowError(ex);
        }
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            _status.Text = "Copied to the clipboard. Paste it into your team chat or an email.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _status.Text = "Couldn't use the clipboard; select the message and copy it by hand.";
        }
    }

    private void ShowError(VaultException ex)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = ex.Code == Protocol.ErrorCodes.Forbidden
            ? ex.Message + " (Only vault admins can invite people.)"
            : ex.Message;
    }
}
