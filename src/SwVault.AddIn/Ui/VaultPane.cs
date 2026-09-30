using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>
    /// The SwVault task pane: pick a vault, browse folders, see each file's state and who has it
    /// checked out, and run commands from the right-click menu.
    /// </summary>
    internal sealed class VaultPane : UserControl
    {
        private readonly AgentConnection _agent;
        private readonly VaultCommands _commands;
        private readonly ComboBox _vaults = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _refresh = new Button { Text = "Refresh", AutoSize = true };
        private readonly Button _connect = new Button { Text = "Connect vault...", AutoSize = true };
        private readonly Label _connection = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, AutoEllipsis = true };
        private readonly TreeView _folders = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ListView _files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
        private readonly Label _active = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(4), AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle };
        private readonly Button _add = new Button { Text = "Add to Vault", Dock = DockStyle.Bottom, Height = 32, Visible = false, BackColor = Color.FromArgb(0, 150, 136), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Timer _debounce = new Timer { Interval = 600 };
        private readonly ImageList _images = Icons.StateImages();
        private VaultInfo[] _vaultList = new VaultInfo[0];
        private string _activePath;

        private static readonly string[] Filters = { "Browse folders", "My check-outs", "Outdated", "In review", "Checked out (anyone)", "New and modified" };

        public VaultPane(AgentConnection agent, VaultCommands commands)
        {
            _agent = agent;
            _commands = commands;
            Font = SystemFonts.MessageBoxFont;
            _folders.ImageList = _images;
            _files.SmallImageList = _images;
            _files.Columns.Add("Name", 150);
            _files.Columns.Add("Status", 120);
            _files.Columns.Add("Ver", 40);
            _files.Columns.Add("Checked out by", 90);
            _files.Columns.Add("State", 70);
            _files.Columns.Add("Rev", 36);
            _filter.Items.AddRange(Filters);
            _filter.SelectedIndex = 0;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(2) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(_vaults, 0, 0);
            top.Controls.Add(_refresh, 1, 0);
            top.Controls.Add(_filter, 0, 1);
            top.Controls.Add(_connect, 1, 1);
            top.Controls.Add(_search, 0, 2);
            top.SetColumnSpan(_search, 2);
            _search.GotFocus += (s, e) => { if (_search.ForeColor == Color.Gray) { _search.Text = ""; _search.ForeColor = SystemColors.WindowText; } };
            SetSearchPlaceholder();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 140 };
            split.Panel1.Controls.Add(_folders);
            split.Panel2.Controls.Add(_files);

            Controls.Add(split);
            Controls.Add(_add);
            Controls.Add(_active);
            Controls.Add(_connection);
            Controls.Add(top);
            split.BringToFront();

            _files.ContextMenuStrip = BuildMenu();
            _files.DoubleClick += (s, e) => Run("Open", () => _commands.OpenAsync(SelectedPaths.First()), requireSelection: true);
            _files.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && SelectedPaths.Count > 0) Run("Open", () => _commands.OpenAsync(SelectedPaths.First()), true); };
            _folders.BeforeExpand += (s, e) => Safe(() => ExpandAsync(e.Node));
            _folders.AfterSelect += (s, e) => Safe(LoadFilesAsync);
            _vaults.SelectedIndexChanged += (s, e) => Safe(LoadRootAsync);
            _filter.SelectedIndexChanged += (s, e) => Safe(LoadFilesAsync);
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe(LoadFilesAsync); } };
            _refresh.Click += (s, e) => Safe(() => RefreshAllAsync(sync: true));
            _connect.Click += (s, e) => Run("Connect vault", () => _commands.SetupVaultAsync());
            _add.Click += (s, e) => AddToVaultRequested?.Invoke();
            _debounce.Tick += (s, e) => { _debounce.Stop(); Safe(async () => { await LoadFilesAsync(); await ShowActiveDocAsync(_activePath); }); };

            _agent.StatusChanged += n => { if (n.VaultId == null || n.VaultId == CurrentVault?.Id) { _debounce.Stop(); _debounce.Start(); } };
            _agent.ConnectionChanged += () => UpdateConnectionLabel();
            _commands.Changed += _ => { _debounce.Stop(); _debounce.Start(); };
        }

        /// <summary>The Add to Vault button was clicked; the add-in adds the active document.</summary>
        public event Action AddToVaultRequested;

        private VaultInfo CurrentVault => _vaults.SelectedIndex >= 0 && _vaults.SelectedIndex < _vaultList.Length ? _vaultList[_vaults.SelectedIndex] : null;

        private List<string> SelectedPaths => _files.SelectedItems.Cast<ListViewItem>().Select(i => ((FileStatusDto)i.Tag).LocalPath).ToList();

        private void SetSearchPlaceholder()
        {
            _search.ForeColor = Color.Gray;
            _search.Text = "Search file names and properties (Enter)";
        }

        private string SearchText => _search.ForeColor == Color.Gray ? "" : _search.Text.Trim();

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => Run("Open", () => _commands.OpenAsync(SelectedPaths.First()), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Get Latest", null, (s, e) => Run("Get latest", () => _commands.GetLatestAsync(SelectedPaths, withReferences: true), true));
            menu.Items.Add("Check Out", null, (s, e) => Run("Check out", () => _commands.CheckOutAsync(SelectedPaths), true));
            menu.Items.Add("Check In...", null, (s, e) => Run("Check in", () => _commands.CheckInAsync(SelectedPaths), true));
            menu.Items.Add("Undo Check Out", null, (s, e) => Run("Undo check-out", () => _commands.UndoCheckOutAsync(SelectedPaths), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("History...", null, (s, e) => Run("History", () => _commands.HistoryAsync(SelectedPaths.First()), true));
            menu.Items.Add("Where Used...", null, (s, e) => Run("Where used", () => _commands.WhereUsedAsync(SelectedPaths.First()), true));
            menu.Items.Add("Change State...", null, (s, e) => Run("Change state", () => _commands.ChangeStateAsync(SelectedPaths.First()), true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show in Explorer", null, (s, e) =>
            {
                var path = SelectedPaths.FirstOrDefault();
                if (path != null) Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + Path.GetDirectoryName(path) + "\"");
            });
            menu.Opening += (s, e) => e.Cancel = SelectedPaths.Count == 0;
            return menu;
        }

        private void Run(string what, Func<Task> body, bool requireSelection = false)
        {
            if (requireSelection && SelectedPaths.Count == 0) return;
            UiThread.Run(what, body);
        }

        /// <summary>Background refreshes: failures show in the status line instead of an error dialog.</summary>
        private async void Safe(Func<Task> body)
        {
            UiThread.EnsureContext();
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                Log.Warn("Task pane: " + ex.Message);
                if (!IsDisposed) _connection.Text = ex.Message;
            }
        }

        // ------------------------------------------------------------ loading

        public async Task RefreshAllAsync(bool sync)
        {
            try
            {
                var selectedId = CurrentVault?.Id;
                if (sync && selectedId != null) await _agent.SyncVaultAsync(selectedId);
                _vaultList = await _agent.GetVaultsAsync();
                _vaults.BeginUpdate();
                _vaults.Items.Clear();
                foreach (var v in _vaultList) _vaults.Items.Add(v.Name + "  (" + v.LocalRoot + ")");
                _vaults.EndUpdate();
                var index = Array.FindIndex(_vaultList, v => v.Id == selectedId);
                if (_vaultList.Length > 0) _vaults.SelectedIndex = index >= 0 ? index : 0;
                else _connection.Text = "No vault connected yet - click 'Connect vault...'.";
                UpdateConnectionLabel();
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
                Log.Warn("Task pane refresh: " + ex.Message);
            }
        }

        private void UpdateConnectionLabel()
        {
            var v = CurrentVault;
            if (!_agent.IsConnected) _connection.Text = "SwVault agent not connected.";
            else if (v == null) return;
            else _connection.Text = (v.Online ? "Online" : "Offline") + " - signed in as " + (v.UserLogin ?? "?") + (v.LastError != null && !v.Online ? " - " + v.LastError : "");
        }

        private async Task LoadRootAsync()
        {
            var vault = CurrentVault;
            _folders.Nodes.Clear();
            if (vault == null) return;
            var root = new TreeNode(vault.Name) { Tag = "", ImageKey = Icons.Folder16, SelectedImageKey = Icons.Folder16 };
            root.Nodes.Add(new TreeNode("...") { Tag = null });
            _folders.Nodes.Add(root);
            root.Expand();
            _folders.SelectedNode = root;
            UpdateConnectionLabel();
            await LoadFilesAsync();
        }

        private async Task ExpandAsync(TreeNode node)
        {
            var vault = CurrentVault;
            if (vault == null || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
            try
            {
                var entries = await _agent.ListFolderAsync(vault.Id, (string)node.Tag);
                node.Nodes.Clear();
                foreach (var folder in entries.Where(e => e.IsFolder))
                {
                    var child = new TreeNode(Path.GetFileName(folder.Path)) { Tag = folder.Path, ImageKey = Icons.Folder16, SelectedImageKey = Icons.Folder16 };
                    child.Nodes.Add(new TreeNode("...") { Tag = null });
                    node.Nodes.Add(child);
                }
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
            }
        }

        private async Task LoadFilesAsync()
        {
            var vault = CurrentVault;
            if (vault == null) return;
            try
            {
                FileStatusDto[] entries;
                var query = SearchText;
                if (_filter.SelectedIndex > 0 || query.Length > 0)
                {
                    var filter = new[] { StatusFilter.All, StatusFilter.MyCheckouts, StatusFilter.Outdated, StatusFilter.InReview, StatusFilter.CheckedOut, StatusFilter.Modified }[Math.Max(0, _filter.SelectedIndex)];
                    entries = await _agent.SearchAsync(vault.Id, query, filter);
                }
                else
                {
                    var folder = _folders.SelectedNode?.Tag as string ?? "";
                    entries = (await _agent.ListFolderAsync(vault.Id, folder)).Where(e => !e.IsFolder).ToArray();
                }
                ShowFiles(entries, showFolder: _filter.SelectedIndex > 0 || query.Length > 0);
            }
            catch (AgentException ex)
            {
                _connection.Text = ex.Message;
            }
        }

        private void ShowFiles(IEnumerable<FileStatusDto> entries, bool showFolder)
        {
            var selected = new HashSet<string>(SelectedPaths, StringComparer.OrdinalIgnoreCase);
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (var e in entries)
            {
                var name = showFolder ? e.Path : Path.GetFileName(e.Path);
                var item = new ListViewItem(new[]
                {
                    name,
                    ShortState(e),
                    e.ServerVersion > 0 ? "v" + e.ServerVersion : "",
                    e.LockState == LockState.None ? "" : e.LockState == LockState.Other ? e.LockOwner : "you",
                    e.State ?? "",
                    e.Revision ?? "",
                })
                {
                    Tag = e,
                    ImageKey = Icons.StateKey(e),
                    ToolTipText = VaultDialog.Describe(e) + (string.IsNullOrEmpty(e.Comment) ? "" : "\nLast comment: " + e.Comment),
                    Selected = selected.Contains(e.LocalPath),
                };
                _files.Items.Add(item);
            }
            _files.ShowItemToolTips = true;
            _files.EndUpdate();
        }

        private static string ShortState(FileStatusDto e)
        {
            switch (e.LocalState)
            {
                case LocalState.UpToDate: return "Up to date";
                case LocalState.Outdated: return "Outdated (v" + e.LocalVersion + ")";
                case LocalState.Modified: return "Modified";
                case LocalState.Conflict: return "Conflict";
                case LocalState.LocalOnly: return "New";
                case LocalState.NotLocal: return "Not local";
                case LocalState.MissingLocally: return "Missing";
                case LocalState.DeletedOnServer: return "Deleted";
                default: return e.LocalState.ToString();
            }
        }

        /// <summary>Shows the active document's vault status at the bottom of the pane.</summary>
        public async Task ShowActiveDocAsync(string path)
        {
            _activePath = path;
            if (path == null)
            {
                _active.Text = "No document open.";
                _active.BackColor = SystemColors.Control;
                _add.Visible = false;
                return;
            }
            if (path.Length == 0)
            {
                _active.Text = "This document hasn't been saved. Add to Vault saves it in your vault folder and checks it in.";
                _active.BackColor = SystemColors.Control;
                _add.Visible = true;
                return;
            }
            try
            {
                var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
                if (path != _activePath) return;
                _add.Visible = status == null || status.LocalState == LocalState.LocalOnly;
                if (status == null)
                {
                    _active.Text = Path.GetFileName(path) + ": not in a vault folder.";
                    _active.BackColor = SystemColors.Control;
                    return;
                }
                _active.Text = Path.GetFileName(path) + "  " + (status.ServerVersion > 0 ? "v" + status.ServerVersion + "  " : "") +
                               (status.State ?? "") + (string.IsNullOrEmpty(status.Revision) ? "" : " rev " + status.Revision) + "\n" + VaultDialog.Describe(status);
                _active.BackColor = status.LocalState == LocalState.Outdated || status.LocalState == LocalState.Conflict ? Color.FromArgb(255, 243, 205)
                    : status.LockState == LockState.MineHere ? Color.FromArgb(217, 234, 250)
                    : status.LockState == LockState.Other ? Color.FromArgb(250, 219, 216)
                    : SystemColors.Control;
            }
            catch (AgentException ex)
            {
                _active.Text = ex.Message;
            }
        }
    }
}
