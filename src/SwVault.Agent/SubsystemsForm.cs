using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Cars and their subsystems (each a vault folder) with their responsible engineers. Anyone can add
/// a car or a subsystem and ask to be a subsystem's RE; an admin approves RE requests.
/// </summary>
internal sealed class SubsystemsForm : Form
{
    private readonly AgentHost _agent;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly Button _addCar = new() { Text = "Add car...", AutoSize = true };
    private readonly Button _addSubsystem = new() { Text = "Add subsystem...", AutoSize = true, Enabled = false };
    private readonly Button _claim = new() { Text = "I'm a responsible engineer of this", AutoSize = true, Enabled = false };
    private readonly Button _stepDown = new() { Text = "Step down", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(640, 0) };
    private string _me = "";

    private sealed record EngineerNode(Subsystem Subsystem, SubsystemEngineer Engineer);

    public SubsystemsForm(AgentHost agent)
    {
        _agent = agent;
        Text = "SwVault - Subsystems";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 700;
        Height = 560;
        MinimumSize = new Size(560, 400);
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 10, 10, 6),
            MaximumSize = new Size(680, 0),
            Text = "Each subsystem is a folder in the vault. Its responsible engineers (REs) are told when others add, check in or release files there, " +
                   "and are copied on review requests from general members. Being an RE needs an admin's approval.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { _addCar, _addSubsystem, _claim, _stepDown, _remove, _refresh });
        var statusBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        statusBar.Controls.Add(_status);
        Controls.Add(_tree);
        Controls.Add(intro);
        Controls.Add(buttons);
        Controls.Add(statusBar);

        _tree.AfterSelect += (_, _) => UpdateButtons();
        _addCar.Click += async (_, _) => await AddCarAsync();
        _addSubsystem.Click += async (_, _) => await AddSubsystemAsync();
        _claim.Click += async (_, _) => await ClaimAsync();
        _stepDown.Click += async (_, _) => await RemoveAsync(self: true);
        _remove.Click += async (_, _) => await RemoveAsync(self: false);
        _refresh.Click += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private Car? SelectedCar => _tree.SelectedNode?.Tag switch
    {
        Car car => car,
        Subsystem s => _cars.FirstOrDefault(c => c.Id == s.CarId),
        EngineerNode e => _cars.FirstOrDefault(c => c.Id == e.Subsystem.CarId),
        _ => null,
    };

    private Subsystem? SelectedSubsystem => _tree.SelectedNode?.Tag switch
    {
        Subsystem s => s,
        EngineerNode e => e.Subsystem,
        _ => null,
    };

    private IReadOnlyList<Car> _cars = Array.Empty<Car>();

    private async Task LoadAsync(string? selectId = null)
    {
        _status.Text = "Loading...";
        try
        {
            var (cars, me) = await Task.Run(() => _agent.CarsAsync());
            _cars = cars;
            _me = me;
            selectId ??= _tree.SelectedNode?.Tag switch { Car c => c.Id, Subsystem s => s.Id, EngineerNode e => e.Subsystem.Id, _ => null };
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            TreeNode? select = null;
            foreach (var car in cars)
            {
                var carNode = new TreeNode($"{car.Name}   [{car.Folder}]") { Tag = car, NodeFont = new Font(_tree.Font, FontStyle.Bold) };
                if (car.Id == selectId) select = carNode;
                foreach (var s in car.Subsystems)
                {
                    var approved = s.Engineers.Where(e => e.Approved).Select(e => e.Login).ToList();
                    var subNode = new TreeNode($"{s.Name}   [{s.Folder}]   RE: {(approved.Count == 0 ? "none yet" : string.Join(", ", approved))}")
                    {
                        Tag = s,
                        ToolTipText = s.Folder,
                    };
                    if (s.IsEngineer(me)) subNode.ForeColor = Color.FromArgb(0, 102, 204);
                    foreach (var e in s.Engineers)
                    {
                        var node = new TreeNode(e.Approved ? e.Login : $"{e.Login}  (waiting for an admin)") { Tag = new EngineerNode(s, e) };
                        if (!e.Approved) node.ForeColor = Color.DarkGoldenrod;
                        subNode.Nodes.Add(node);
                    }
                    carNode.Nodes.Add(subNode);
                    if (s.Id == selectId) select = subNode;
                }
                _tree.Nodes.Add(carNode);
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            if (select != null) _tree.SelectedNode = select;
            _status.Text = cars.Count == 0 ? "No cars yet. Add one, then add its subsystems." : "";
            UpdateButtons();
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void UpdateButtons()
    {
        var subsystem = SelectedSubsystem;
        _addSubsystem.Enabled = SelectedCar != null;
        _claim.Enabled = subsystem != null && !subsystem.Engineers.Any(e => string.Equals(e.Login, _me, StringComparison.OrdinalIgnoreCase));
        var mineEntry = subsystem?.Engineers.FirstOrDefault(e => string.Equals(e.Login, _me, StringComparison.OrdinalIgnoreCase));
        _stepDown.Enabled = mineEntry != null;
        _stepDown.Text = mineEntry is { Approved: false } ? "Withdraw my request" : "Step down";
        _remove.Enabled = _tree.SelectedNode?.Tag is EngineerNode other && _agent.TeamWatcher.IsAdmin
                          && !string.Equals(other.Engineer.Login, _me, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddCarAsync()
    {
        using var prompt = new NamePrompt("Add a car", "Car name (e.g. 2027 Car):", "Vault folder (leave empty to use the name):");
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(() => _agent.AddCarAsync(prompt.NameText, prompt.FolderText), $"Added {prompt.NameText}.");
    }

    private async Task AddSubsystemAsync()
    {
        var car = SelectedCar;
        if (car == null) return;
        using var prompt = new NamePrompt($"Add a subsystem to {car.Name}", "Subsystem name (e.g. Front Suspension):",
            $"Vault folder (leave empty for {car.Folder}/<name>):");
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(() => _agent.AddSubsystemAsync(car.Id, prompt.NameText, prompt.FolderText), $"Added {prompt.NameText} to {car.Name}. Its folder is in your vault folder.");
    }

    private async Task ClaimAsync()
    {
        var subsystem = SelectedSubsystem;
        if (subsystem == null) return;
        _status.Text = "Sending...";
        try
        {
            var approved = await Task.Run(() => _agent.ClaimEngineerAsync(subsystem.Id));
            await LoadAsync(subsystem.Id);
            _status.Text = approved
                ? $"You're a responsible engineer of {subsystem.Name}."
                : $"Asked to be a responsible engineer of {subsystem.Name}. An admin has to approve it; SwVault tells you when they do.";
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task RemoveAsync(bool self)
    {
        var subsystem = SelectedSubsystem;
        if (subsystem == null) return;
        var login = self ? _me : (_tree.SelectedNode?.Tag as EngineerNode)?.Engineer.Login;
        if (login == null) return;
        var question = self ? $"Stop being a responsible engineer of {subsystem.Name}?" : $"Remove {login} as a responsible engineer of {subsystem.Name}?";
        if (MessageBox.Show(this, question, "SwVault", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAsync(() => _agent.RemoveEngineerAsync(subsystem.Id, login), self ? $"You're no longer an RE of {subsystem.Name}." : $"Removed {login}.");
    }

    private async Task RunAsync(Func<Task> action, string done)
    {
        _status.Text = "Saving...";
        try
        {
            await Task.Run(action);
            await LoadAsync();
            _status.Text = done;
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }
}

/// <summary>Asks for a name and an optional folder.</summary>
internal sealed class NamePrompt : Form
{
    private readonly TextBox _name = new() { Width = 320 };
    private readonly TextBox _folder = new() { Width = 320 };

    public NamePrompt(string title, string nameLabel, string folderLabel)
    {
        Text = "SwVault - " + title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;
        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
        layout.Controls.Add(new Label { Text = nameLabel, AutoSize = true });
        layout.Controls.Add(_name);
        layout.Controls.Add(new Label { Text = folderLabel, AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(_folder);
        var ok = new Button { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 320, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (_name.Text.Trim().Length > 0) return;
            DialogResult = DialogResult.None;
            _name.Focus();
        };
    }

    public string NameText => _name.Text.Trim();
    public string? FolderText => _folder.Text.Trim().Length == 0 ? null : _folder.Text.Trim();
}
