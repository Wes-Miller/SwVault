using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>"General member" or "Subteam lead of [subteam]", used at sign-up and in My Team Role.</summary>
internal sealed class TeamRolePicker : FlowLayoutPanel
{
    private readonly RadioButton _member = new() { Text = "General member", AutoSize = true, Checked = true };
    private readonly RadioButton _lead = new() { Text = "Subteam lead of", AutoSize = true };
    private readonly ComboBox _subteam = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDown, Enabled = false };

    public TeamRolePicker()
    {
        AutoSize = true;
        WrapContents = false;
        FlowDirection = FlowDirection.LeftToRight;
        Margin = new Padding(0);
        _subteam.Margin = new Padding(0, 1, 0, 0);
        Controls.Add(_member);
        Controls.Add(_lead);
        Controls.Add(_subteam);
        _lead.CheckedChanged += (_, _) =>
        {
            _subteam.Enabled = _lead.Checked;
            if (_lead.Checked) _subteam.Focus();
        };
    }

    /// <summary>Subteams others already lead, offered in the list (you can still type a new one).</summary>
    public void SetSuggestions(IEnumerable<string> subteams)
    {
        var typed = _subteam.Text;
        _subteam.Items.Clear();
        foreach (var s in subteams) _subteam.Items.Add(s);
        _subteam.Text = typed;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public TeamProfile Profile
    {
        get => new(_lead.Checked, _lead.Checked ? _subteam.Text.Trim() : null);
        set
        {
            _lead.Checked = value.IsLead;
            _member.Checked = !value.IsLead;
            _subteam.Text = value.Subteam ?? "";
        }
    }

    /// <summary>Null when valid, else what to fix.</summary>
    public string? Problem => _lead.Checked && _subteam.Text.Trim().Length == 0 ? "Say which subteam you lead (e.g. Chassis, Aero, Powertrain)." : null;

    public void SetEnabled(bool enabled)
    {
        _member.Enabled = _lead.Enabled = enabled;
        _subteam.Enabled = enabled && _lead.Checked;
    }
}
