using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>My team role: general member, or lead of a subteam (who then gets review requests).</summary>
internal sealed class ProfileForm : Form
{
    private readonly AgentHost _agent;
    private readonly TeamRolePicker _role = new();
    private readonly Button _save = new() { Text = "Save", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };

    public ProfileForm(AgentHost agent, bool firstTime = false)
    {
        _agent = agent;
        Text = "SwVault - My team role";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;
        TopMost = firstTime;

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14) };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = (firstTime ? "One more thing: " : "") +
                   "are you a general member or a subteam lead? Leads are who teammates send design, simulation and drawing review requests to. " +
                   "An admin approves new leads. (Responsible engineers of subsystems are set in tray icon > Subsystems.)",
        });
        layout.Controls.Add(_role);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 440, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_save);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_status);
        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = _cancel;

        _save.Click += async (_, _) => await SaveAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Loading...";
        try
        {
            var (directory, me) = await Task.Run(() => _agent.TeamDirectoryAsync());
            _role.SetSuggestions(directory.Subteams);
            var person = directory.People.FirstOrDefault(p => string.Equals(p.Login, me, StringComparison.OrdinalIgnoreCase));
            if (person?.Profile != null) _role.Profile = person.Profile;
            _status.Text = person?.PendingLead != null
                ? $"You asked to be the {person.PendingLead} lead; an admin still has to approve it."
                : "Becoming a lead needs an admin's approval.";
            _save.Enabled = true;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (_role.Problem != null)
        {
            _status.Text = _role.Problem;
            return;
        }
        _save.Enabled = false;
        try
        {
            var profile = _role.Profile;
            var pending = await Task.Run(() => _agent.SaveMyProfileAsync(profile));
            if (pending != null)
                MessageBox.Show(this, $"Your request to be the {pending} lead was sent to the vault admins. SwVault tells you when it's approved; until then you're listed as before.",
                    "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
            _save.Enabled = true;
        }
    }
}
