using SwVault.Core;
using SwVault.Core.Auth;

namespace SwVault.Agent;

/// <summary>Lists connected vaults and connects new ones (URL, folder, user, token).</summary>
internal sealed class VaultsForm : Form
{
    private readonly AgentHost _agent;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly TextBox _url = new() { Width = 360, PlaceholderText = "https://git.example.edu/fsae/vault.git" };
    private readonly TextBox _root = new() { Width = 360, PlaceholderText = "(use the vault's shared folder)" };
    private readonly TextBox _user = new() { Width = 170 };
    private readonly TextBox _token = new() { Width = 170, UseSystemPasswordChar = true };
    private readonly Button _add = new() { Text = "Connect", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };

    public VaultsForm(AgentHost agent)
    {
        _agent = agent;
        Text = "SwVault - Vaults";
        Width = 760;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();

        _list.Columns.Add("Vault", 140);
        _list.Columns.Add("Folder", 220);
        _list.Columns.Add("Server", 360);

        var form = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        form.Controls.Add(new Label { Text = "Repository URL", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        form.Controls.Add(_url, 1, 0);
        form.Controls.Add(new Label { Text = "Vault folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        form.Controls.Add(_root, 1, 1);
        form.Controls.Add(new Label { Text = "User name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        form.Controls.Add(_user, 1, 2);
        form.Controls.Add(new Label { Text = "Access token", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        form.Controls.Add(_token, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(_add);
        buttons.Controls.Add(_status);
        form.Controls.Add(buttons, 1, 4);

        Controls.Add(_list);
        Controls.Add(form);
        _add.Click += async (_, _) => await ConnectAsync();
        Load += (_, _) => Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var r in _agent.Vaults.Registrations)
            _list.Items.Add(new ListViewItem(new[] { r.Name ?? r.Id, r.LocalRoot, r.RemoteUrl }));
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_url.Text)) return;
        _add.Enabled = false;
        _status.Text = "Connecting...";
        try
        {
            var credential = _user.Text.Length > 0 && _token.Text.Length > 0 ? new Credential(_user.Text.Trim(), _token.Text.Trim()) : null;
            var session = await Task.Run(() => _agent.Vaults.AddAsync(_url.Text.Trim(), string.IsNullOrWhiteSpace(_root.Text) ? null : _root.Text.Trim(), credential));
            _status.Text = $"Connected to {session.Config.Name} as {session.User?.Login}.";
            _token.Clear();
            _agent.Sync.Poke();
            Reload();
        }
        catch (VaultException ex)
        {
            _status.Text = "";
            MessageBox.Show(this, ex.Message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _add.Enabled = true;
        }
    }
}
