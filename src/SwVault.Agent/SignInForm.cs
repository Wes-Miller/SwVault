using SwVault.Core;

namespace SwVault.Agent;

/// <summary>
/// First-run sign-in for a team install (team.json): user name and password, nothing else.
/// The agent turns the password into an access token and then downloads the vault.
/// </summary>
internal sealed class SignInForm : Form
{
    private readonly AgentHost _agent;
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Button _signIn = new() { Text = "Sign in", AutoSize = true };
    private readonly Button _later = new() { Text = "Later", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(380, 0) };

    public SignInForm(AgentHost agent)
    {
        _agent = agent;
        var team = agent.Team!;
        Text = "SwVault - Sign in to " + team.Name;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;
        TopMost = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(14) };
        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Margin = new Padding(0, 0, 0, 12),
            Text = $"Sign in with the user name and password your team admin gave you. SwVault then downloads the {team.Name} files to {team.LocalRoot}.",
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(new Label { Text = "User name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_user, 1, 1);
        layout.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_password, 1, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(_later);
        buttons.Controls.Add(_signIn);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(_status, 0, 4);
        layout.SetColumnSpan(_status, 2);
        Controls.Add(layout);

        AcceptButton = _signIn;
        CancelButton = _later;
        _signIn.Click += async (_, _) => await SignInAsync();
    }

    private async Task SignInAsync()
    {
        var user = _user.Text.Trim();
        if (user.Length == 0 || _password.Text.Length == 0)
        {
            _status.Text = "Enter your user name and password.";
            return;
        }
        _signIn.Enabled = _later.Enabled = _user.Enabled = _password.Enabled = false;
        _status.Text = "Signing in...";
        try
        {
            var password = _password.Text;
            await Task.Run(() => _agent.JoinTeamAsync(user, password));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
            _password.SelectAll();
        }
        catch (Exception ex)
        {
            _agent.Log.Error("Sign-in failed", ex);
            _status.Text = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed)
            {
                _signIn.Enabled = _later.Enabled = _user.Enabled = _password.Enabled = true;
                _password.Clear();
                _password.Focus();
            }
        }
    }
}
