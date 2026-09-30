using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// First-run sign-in for a team install (team.json). Two ways in:
/// - "I have an account": user name and password.
/// - "I'm new": an invite code (usually filled in already from the download link), then choose a
///   user name and password. The account is created on the spot.
/// Either way the agent then turns the password into an access token and downloads the vault.
/// </summary>
internal sealed class SignInForm : Form
{
    private readonly AgentHost _agent;
    private readonly TeamConfig _team;
    private readonly RadioButton _existing = new() { Text = "I have an account", AutoSize = true };
    private readonly RadioButton _joining = new() { Text = "I'm new and have an invite", AutoSize = true };
    private readonly TextBox _code = new() { Width = 260, CharacterCasing = CharacterCasing.Upper, PlaceholderText = "XXXX-XXXX-XXXX" };
    private readonly Label _codeInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _email = new() { Width = 180 };
    private readonly Button _sendCode = new() { Text = "Email me a code", AutoSize = true };
    private readonly TextBox _emailCode = new() { Width = 100, PlaceholderText = "6 digits", MaxLength = 7 };
    private readonly Label _emailInfo = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(300, 0) };
    private readonly TextBox _fullName = new() { Width = 260, PlaceholderText = "e.g. Alex Smith" };
    private readonly TeamRolePicker _role = new();
    private Control[] _emailOnly = Array.Empty<Control>();
    private InviteDescription? _invite;
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly TextBox _confirm = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Label _userLabel = new() { Text = "User name", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _passwordLabel = new() { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Control[] _joinOnly;
    private readonly Button _signIn = new() { Text = "Sign in", AutoSize = true };
    private readonly Button _later = new() { Text = "Later", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(400, 0) };
    private CancellationTokenSource? _describe;

    public SignInForm(AgentHost agent)
    {
        _agent = agent;
        _team = agent.Team!;
        Text = "SwVault - " + _team.Name;
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
        void Span(Control c, Padding margin)
        {
            c.Margin = margin;
            layout.Controls.Add(c);
            layout.SetColumnSpan(c, 2);
        }
        void Row(Control label, Control box)
        {
            layout.Controls.Add(label);
            layout.Controls.Add(box);
        }

        Span(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = $"Welcome to the {_team.Name} vault. After you sign in, SwVault downloads the files to {_team.LocalRoot}.",
        }, new Padding(0, 0, 0, 10));
        var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        modes.Controls.Add(_existing);
        modes.Controls.Add(_joining);
        Span(modes, new Padding(0, 0, 0, 8));

        var codeLabel = new Label { Text = "Invite code", AutoSize = true, Anchor = AnchorStyles.Left };
        var emailLabel = new Label { Text = "School email", AutoSize = true, Anchor = AnchorStyles.Left };
        var emailCodeLabel = new Label { Text = "Code from the email", AutoSize = true, Anchor = AnchorStyles.Left };
        var nameLabel = new Label { Text = "Your name", AutoSize = true, Anchor = AnchorStyles.Left };
        var roleLabel = new Label { Text = "On the team I'm a", AutoSize = true, Anchor = AnchorStyles.Left };
        var confirmLabel = new Label { Text = "Confirm password", AutoSize = true, Anchor = AnchorStyles.Left };
        var spacer = new Label { AutoSize = true };
        var spacer2 = new Label { AutoSize = true };
        var emailRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        emailRow.Controls.Add(_email);
        emailRow.Controls.Add(_sendCode);
        Row(codeLabel, _code);
        Row(spacer, _codeInfo);
        Row(emailLabel, emailRow);
        Row(emailCodeLabel, _emailCode);
        Row(spacer2, _emailInfo);
        Row(nameLabel, _fullName);
        Row(roleLabel, _role);
        Row(_userLabel, _user);
        Row(_passwordLabel, _password);
        Row(confirmLabel, _confirm);
        _emailOnly = new Control[] { emailLabel, emailRow, emailCodeLabel, _emailCode, spacer2, _emailInfo };
        _joinOnly = new Control[] { codeLabel, _code, spacer, _codeInfo, nameLabel, _fullName, roleLabel, _role, confirmLabel, _confirm };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(_later);
        buttons.Controls.Add(_signIn);
        Span(buttons, new Padding(0, 10, 0, 0));
        Span(_status, new Padding(0, 4, 0, 0));
        Controls.Add(layout);

        AcceptButton = _signIn;
        CancelButton = _later;
        _existing.CheckedChanged += (_, _) => ApplyMode();
        _joining.CheckedChanged += (_, _) => ApplyMode();
        _code.TextChanged += async (_, _) => await DescribeCodeAsync();
        _sendCode.Click += async (_, _) => await SendEmailCodeAsync();
        _signIn.Click += async (_, _) => await SubmitAsync();

        var pending = agent.PendingInviteCode();
        if (pending != null) _code.Text = pending;
        (pending != null ? _joining : _existing).Checked = true;
        ApplyMode();
    }

    private bool Joining => _joining.Checked;

    private void ApplyMode()
    {
        SuspendLayout();
        foreach (var c in _joinOnly) c.Visible = Joining;
        foreach (var c in _emailOnly) c.Visible = Joining && _invite?.NeedsEmail == true;
        _userLabel.Text = Joining ? "Choose a user name" : "User name";
        _passwordLabel.Text = Joining ? "Choose a password" : "Password";
        _signIn.Text = Joining ? "Create account" : "Sign in";
        _status.Text = "";
        ResumeLayout(true);
        (Joining && _code.Text.Length == 0 ? _code : Joining ? _fullName : _user).Focus();
    }

    /// <summary>Shows what a complete code is for (team and access), as soon as it's typed.</summary>
    private async Task DescribeCodeAsync()
    {
        _describe?.Cancel();
        var code = TeamInvites.NormalizeCode(_code.Text);
        if (code == null)
        {
            _codeInfo.Text = _code.Text.Length == 0 ? "From your invite message." : "";
            SetInvite(null);
            return;
        }
        var cts = _describe = new CancellationTokenSource();
        _codeInfo.Text = "Checking...";
        try
        {
            var info = await Task.Run(() => TeamInvites.DescribeAsync(_team.VaultUrl, code, cts.Token));
            if (cts.IsCancellationRequested || IsDisposed) return;
            _codeInfo.ForeColor = info == null ? Color.Firebrick : Color.DarkGreen;
            _codeInfo.Text = info == null
                ? "This code isn't valid anymore. Ask your admin for a new one."
                : $"Joins {info.Team} with {(info.Role == "viewer" ? "view-only" : "check out / check in")} access.";
            SetInvite(info);
        }
        catch (Exception ex) when (ex is VaultException or OperationCanceledException)
        {
            if (!cts.IsCancellationRequested && !IsDisposed)
            {
                _codeInfo.ForeColor = Color.DimGray;
                _codeInfo.Text = ex.Message;
            }
        }
    }

    private void SetInvite(InviteDescription? invite)
    {
        _invite = invite;
        if (invite != null)
        {
            _role.SetSuggestions(invite.Subteams);
            _email.PlaceholderText = invite.EmailDomains.Count > 0 ? "you@" + invite.EmailDomains[0] : "";
            _emailInfo.Text = invite.NeedsEmail ? $"Use your {string.Join(" or ", invite.EmailDomains.Select(d => "@" + d))} address; we'll email you a 6-digit code." : "";
        }
        SuspendLayout();
        foreach (var c in _emailOnly) c.Visible = Joining && invite?.NeedsEmail == true;
        ResumeLayout(true);
    }

    private async Task SendEmailCodeAsync()
    {
        var code = TeamInvites.NormalizeCode(_code.Text);
        var email = _email.Text.Trim();
        if (code == null) { _emailInfo.Text = "Enter the invite code first."; return; }
        if (email.Length == 0) { _emailInfo.Text = "Enter your school email address."; return; }
        _sendCode.Enabled = false;
        _emailInfo.ForeColor = Color.DimGray;
        _emailInfo.Text = "Sending...";
        try
        {
            await Task.Run(() => TeamInvites.SendEmailCodeAsync(_team.VaultUrl, code, email));
            _emailInfo.ForeColor = Color.DarkGreen;
            _emailInfo.Text = $"Sent to {email}. It can take a minute; check your junk folder too.";
            _emailCode.Focus();
        }
        catch (VaultException ex)
        {
            _emailInfo.ForeColor = Color.Firebrick;
            _emailInfo.Text = ex.Message;
        }
        finally
        {
            if (!IsDisposed) _sendCode.Enabled = true;
        }
    }

    private async Task SubmitAsync()
    {
        var user = _user.Text.Trim();
        var password = _password.Text;
        string? code = null;
        if (Joining)
        {
            code = TeamInvites.NormalizeCode(_code.Text);
            if (code == null) { _status.Text = "Enter the invite code from your invite message (like K7QM-R3XT-9BWE)."; return; }
            if (user.Length == 0) { _status.Text = "Choose a user name, e.g. your first name or initials."; return; }
            if (password.Length < 8) { _status.Text = "Choose a password of at least 8 characters."; return; }
            if (password != _confirm.Text) { _status.Text = "The two passwords don't match."; return; }
            if (_role.Problem != null) { _status.Text = _role.Problem; return; }
            if (_invite?.NeedsEmail == true && _emailCode.Text.Trim().Length == 0) { _status.Text = "Get a code sent to your school email and type it in."; return; }
        }
        else if (user.Length == 0 || password.Length == 0)
        {
            _status.Text = "Enter your user name and password.";
            return;
        }

        SetBusy(true);
        _status.Text = Joining ? "Creating your account..." : "Signing in...";
        try
        {
            var fullName = _fullName.Text.Trim();
            if (code != null)
            {
                var email = _invite?.NeedsEmail == true ? _email.Text.Trim() : null;
                var emailCode = _invite?.NeedsEmail == true ? _emailCode.Text.Trim() : null;
                var profile = _role.Profile;
                await Task.Run(() => _agent.JoinWithInviteAsync(code, user, password, fullName.Length > 0 ? fullName : null, email, emailCode, profile));
            }
            else
                await Task.Run(() => _agent.JoinTeamAsync(user, password));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _agent.Log.Error("Sign-in failed", ex);
            _status.Text = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (Control c in new Control[] { _signIn, _later, _existing, _joining, _code, _email, _sendCode, _emailCode, _fullName, _user, _password, _confirm })
            c.Enabled = !busy;
        _role.SetEnabled(!busy);
        UseWaitCursor = busy;
    }
}
