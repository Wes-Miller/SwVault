using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>Ask a subteam lead for a design, simulation or drawing review of one vault file.</summary>
internal sealed class RequestReviewForm : Form
{
    private readonly AgentHost _agent;
    private readonly string _localPath;
    private readonly Label _file = new() { AutoSize = true, MaximumSize = new Size(380, 0), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _fileNote = new() { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.DarkGoldenrod };
    private readonly RadioButton _design = new() { Text = "Design", AutoSize = true };
    private readonly RadioButton _simulation = new() { Text = "Simulation", AutoSize = true };
    private readonly RadioButton _drawing = new() { Text = "Drawing", AutoSize = true };
    private readonly ComboBox _lead = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox _message = new() { Multiline = true, Width = 380, Height = 90, ScrollBars = ScrollBars.Vertical, PlaceholderText = "What should they look at? (optional)" };
    private readonly Button _send = new() { Text = "Send request", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0) };

    private sealed record LeadItem(TeamPerson Person)
    {
        public override string ToString() => $"{Person.DisplayName} - {Person.Profile?.Subteam} lead";
    }

    public RequestReviewForm(AgentHost agent, string localPath)
    {
        _agent = agent;
        _localPath = localPath;
        Text = "SwVault - Request a review";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;
        TopMost = true;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(14) };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 10, 0) });
            grid.Controls.Add(control);
        }
        _file.Text = Path.GetFileName(localPath);
        var fileBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 6, 0, 0) };
        fileBox.Controls.Add(_file);
        fileBox.Controls.Add(_fileNote);
        Row("File", fileBox);
        var kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        kinds.Controls.AddRange(new Control[] { _design, _simulation, _drawing });
        Row("Review", kinds);
        Row("Lead", _lead);
        Row("Message", _message);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_send);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(_status);
        grid.SetColumnSpan(_status, 2);
        Controls.Add(grid);
        AcceptButton = _send;
        CancelButton = _cancel;

        (Reviews.DefaultKindFor(localPath) == ReviewKind.Drawing ? _drawing : _design).Checked = true;
        _send.Click += async (_, _) => await SendAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private ReviewKind Kind => _simulation.Checked ? ReviewKind.Simulation : _drawing.Checked ? ReviewKind.Drawing : ReviewKind.Design;

    private async Task LoadAsync()
    {
        _status.Text = "Loading...";
        try
        {
            var subject = await Task.Run(() => _agent.ReviewSubjectAsync(_localPath));
            _file.Text = $"{Path.GetFileName(_localPath)}  (version {subject.Version})";
            if (subject.ModifiedLocally)
                _fileNote.Text = "You have changes that aren't checked in. The lead will review the checked-in version; check in first if they should see your latest work.";

            var (directory, me) = await Task.Run(() => _agent.TeamDirectoryAsync());
            var leads = directory.Leads.Where(p => !string.Equals(p.Login, me, StringComparison.OrdinalIgnoreCase)).ToList();
            _lead.Items.Clear();
            foreach (var lead in leads) _lead.Items.Add(new LeadItem(lead));
            if (leads.Count == 0)
            {
                _status.Text = "Nobody has said they're a subteam lead yet. Leads set this with the tray icon > My team role.";
                return;
            }
            _lead.SelectedIndex = 0;
            _status.Text = "";
            _send.Enabled = true;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task SendAsync()
    {
        if (_lead.SelectedItem is not LeadItem lead) return;
        _send.Enabled = false;
        _status.Text = "Sending...";
        try
        {
            var kind = Kind;
            var message = _message.Text;
            await Task.Run(() => _agent.RequestReviewAsync(_localPath, kind, lead.Person.Login, message));
            MessageBox.Show(this, $"{lead.Person.DisplayName} has been asked for a {kind.ToString().ToLowerInvariant()} review of {Path.GetFileName(_localPath)}. SwVault tells you when they respond.",
                "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
            _send.Enabled = true;
        }
    }
}
