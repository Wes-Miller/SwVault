using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn.Ui
{
    /// <summary>Common look for SwVault dialogs.</summary>
    internal abstract class VaultDialog : Form
    {
        protected readonly Button OkButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 28) };
        protected readonly Button CancelButtonControl = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
        protected readonly FlowLayoutPanel Buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };

        protected VaultDialog(string title, int width, int height)
        {
            Text = title;
            Width = width;
            Height = height;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = SystemFonts.MessageBoxFont;
            AcceptButton = OkButton;
            CancelButton = CancelButtonControl;
            Buttons.Controls.Add(CancelButtonControl);
            Buttons.Controls.Add(OkButton);
            Controls.Add(Buttons);
        }

        public static string Describe(FileStatusDto s)
        {
            if (s == null) return "";
            string local;
            switch (s.LocalState)
            {
                case LocalState.NotLocal: local = "Not local"; break;
                case LocalState.UpToDate: local = "Up to date"; break;
                case LocalState.Outdated: local = "Outdated (v" + s.LocalVersion + ", latest v" + s.ServerVersion + ")"; break;
                case LocalState.Modified: local = "Modified"; break;
                case LocalState.Conflict: local = "Conflict: changed locally and on the server"; break;
                case LocalState.LocalOnly: local = "New (not in vault)"; break;
                case LocalState.MissingLocally: local = "Missing locally"; break;
                case LocalState.DeletedOnServer: local = "Deleted from vault"; break;
                default: local = s.LocalState.ToString(); break;
            }
            switch (s.LockState)
            {
                case LockState.MineHere: local += " - checked out by you"; break;
                case LockState.MineElsewhere: local += " - checked out by you on another PC"; break;
                case LockState.Other: local += " - checked out by " + s.LockOwner; break;
            }
            return local;
        }
    }

    /// <summary>Pick files to check in, add a comment.</summary>
    internal sealed class CheckInDialog : VaultDialog
    {
        private readonly ListView _files = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
        private readonly TextBox _comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
        private readonly CheckBox _keep = new CheckBox { Text = "Keep checked out", AutoSize = true };

        public CheckInDialog(IEnumerable<(FileStatusDto Status, bool Checked)> files, string title = "Check In")
            : base(title, 720, 460)
        {
            _files.Columns.Add("File", 330);
            _files.Columns.Add("Status", 340);
            foreach (var (status, check) in files)
            {
                var item = new ListViewItem(new[] { Path.GetFileName(status.LocalPath), Describe(status) }) { Checked = check, Tag = status.LocalPath, ToolTipText = status.LocalPath };
                _files.Items.Add(item);
            }
            _files.ShowItemToolTips = true;

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(8, 4, 8, 0) };
            bottom.Controls.Add(new Label { Text = "Comment (what changed and why):", AutoSize = true });
            bottom.Controls.Add(_comment);
            bottom.Controls.Add(_keep);
            Controls.Add(_files);
            Controls.Add(bottom);
            _files.BringToFront();
            OkButton.Text = title;
            OkButton.Click += (s, e) =>
            {
                if (SelectedPaths.Count == 0)
                {
                    DialogResult = DialogResult.None;
                    MessageBox.Show(this, "Select at least one file.", "SwVault");
                }
            };
        }

        public List<string> SelectedPaths => _files.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
        public string Comment => _comment.Text.Trim();
        public bool KeepCheckedOut => _keep.Checked;
    }

    /// <summary>Check out an assembly or drawing, optionally with some of its references.</summary>
    internal sealed class CheckOutDialog : VaultDialog
    {
        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true };

        public CheckOutDialog(ReferenceNodeDto root) : base("Check Out", 640, 480)
        {
            var label = new Label { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 8, 8, 0), Text = "Tick the files you want to edit. Referenced files you don't tick stay read-only." };
            _tree.Nodes.Add(Build(root, isRoot: true));
            _tree.ExpandAll();
            Controls.Add(_tree);
            Controls.Add(label);
            _tree.BringToFront();
            OkButton.Text = "Check Out";
        }

        private static TreeNode Build(ReferenceNodeDto node, bool isRoot)
        {
            var status = node.Status;
            var blocked = status != null && status.LockState == LockState.Other;
            var text = Path.GetFileName(node.LocalPath) + "   (" + Describe(status) + ")";
            var tn = new TreeNode(text) { Tag = node.LocalPath, Checked = isRoot && !blocked };
            if (blocked) tn.ForeColor = Color.Gray;
            foreach (var child in node.Children ?? new ReferenceNodeDto[0]) tn.Nodes.Add(Build(child, false));
            return tn;
        }

        public List<string> SelectedPaths
        {
            get
            {
                var result = new List<string>();
                void Walk(TreeNodeCollection nodes)
                {
                    foreach (TreeNode n in nodes)
                    {
                        if (n.Checked && !result.Contains((string)n.Tag, StringComparer.OrdinalIgnoreCase)) result.Add((string)n.Tag);
                        Walk(n.Nodes);
                    }
                }
                Walk(_tree.Nodes);
                return result;
            }
        }
    }

    /// <summary>Version history with get-version and rollback.</summary>
    internal sealed class HistoryDialog : VaultDialog
    {
        public enum HistoryAction { None, GetVersion, Rollback }

        private readonly ListView _versions = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        private readonly CheckBox _asBuilt = new CheckBox { Text = "Also get the referenced files as they were", AutoSize = true, Checked = true };
        private readonly Button _get = new Button { Text = "Get This Version", AutoSize = true };
        private readonly Button _rollback = new Button { Text = "Roll Back To This Version", AutoSize = true };

        public HistoryDialog(string path, VersionInfoDto[] versions, bool checkedOutByMe, bool hasReferences)
            : base("History - " + Path.GetFileName(path), 820, 460)
        {
            _versions.Columns.Add("Version", 64);
            _versions.Columns.Add("Date", 150);
            _versions.Columns.Add("By", 100);
            _versions.Columns.Add("State", 80);
            _versions.Columns.Add("Rev", 45);
            _versions.Columns.Add("Comment", 340);
            foreach (var v in versions)
            {
                DateTime at;
                var when = DateTime.TryParse(v.At, out at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : v.At;
                _versions.Items.Add(new ListViewItem(new[] { "v" + v.Version, when, v.By, v.State, v.Revision, v.Comment }) { Tag = v });
            }
            Controls.Add(_versions);
            _versions.BringToFront();

            OkButton.Visible = false;
            CancelButtonControl.Text = "Close";
            if (hasReferences) Buttons.Controls.Add(_asBuilt);
            Buttons.Controls.Add(_rollback);
            Buttons.Controls.Add(_get);
            _rollback.Enabled = false;
            _get.Enabled = false;
            _versions.SelectedIndexChanged += (s, e) =>
            {
                var selected = SelectedVersion != null;
                _get.Enabled = selected && !checkedOutByMe;
                _rollback.Enabled = selected && checkedOutByMe;
            };
            _get.Click += (s, e) => { Action = HistoryAction.GetVersion; DialogResult = DialogResult.OK; };
            _rollback.Click += (s, e) => { Action = HistoryAction.Rollback; DialogResult = DialogResult.OK; };
            if (!checkedOutByMe) new ToolTip().SetToolTip(_rollback, "Check the file out first to roll back.");
        }

        public HistoryAction Action { get; private set; }
        public VersionInfoDto SelectedVersion => _versions.SelectedItems.Count == 0 ? null : (VersionInfoDto)_versions.SelectedItems[0].Tag;
        public bool AsBuilt => _asBuilt.Checked;
    }

    /// <summary>Choose a workflow transition.</summary>
    internal sealed class TransitionDialog : VaultDialog
    {
        private readonly List<RadioButton> _options = new List<RadioButton>();
        private readonly TextBox _comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 60 };

        public TransitionDialog(string path, string currentState, string revision, TransitionOptionDto[] options)
            : base("Change State - " + Path.GetFileName(path), 560, 380)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false, AutoScroll = true };
            panel.Controls.Add(new Label { Text = "Current state: " + currentState + (string.IsNullOrEmpty(revision) ? "" : "   Revision: " + revision), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            foreach (var option in options)
            {
                var text = option.Name + "  ->  " + option.To + (option.BumpRevision ? "  (revision " + option.NextRevision + ")" : "");
                if (!option.Allowed) text += "   [" + option.Reason + "]";
                var radio = new RadioButton { Text = text, AutoSize = true, Enabled = option.Allowed, Tag = option };
                _options.Add(radio);
                panel.Controls.Add(radio);
                foreach (var warning in option.Warnings ?? new string[0])
                    panel.Controls.Add(new Label { Text = "    ! " + warning, AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(500, 0) });
            }
            if (options.Length == 0) panel.Controls.Add(new Label { Text = "No workflow actions are available from this state.", AutoSize = true });
            var first = _options.FirstOrDefault(o => o.Enabled);
            if (first != null) first.Checked = true;

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
            bottom.Controls.Add(new Label { Text = "Comment:", AutoSize = true });
            bottom.Controls.Add(_comment);
            Controls.Add(panel);
            Controls.Add(bottom);
            panel.BringToFront();
            OkButton.Enabled = first != null;
        }

        public TransitionOptionDto Selected => _options.FirstOrDefault(o => o.Checked)?.Tag as TransitionOptionDto;
        public string Comment => _comment.Text.Trim();
    }

    /// <summary>Shows a list of files (where used, warnings); double-click opens one.</summary>
    internal sealed class FileListDialog : VaultDialog
    {
        private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        public FileListDialog(string title, string caption, IEnumerable<string> files) : base(title, 620, 380)
        {
            Controls.Add(_list);
            Controls.Add(new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0), Text = caption });
            _list.BringToFront();
            foreach (var f in files) _list.Items.Add(f);
            OkButton.Text = "Open";
            OkButton.Enabled = false;
            CancelButtonControl.Text = "Close";
            _list.SelectedIndexChanged += (s, e) => OkButton.Enabled = _list.SelectedItem != null;
            _list.DoubleClick += (s, e) => { if (_list.SelectedItem != null) DialogResult = DialogResult.OK; };
        }

        public string SelectedFile => _list.SelectedItem as string;
    }

    /// <summary>Connect this PC to a vault.</summary>
    internal sealed class VaultSetupDialog : VaultDialog
    {
        private readonly TextBox _url = new TextBox { Width = 380 };
        private readonly TextBox _root = new TextBox { Width = 380 };
        private readonly TextBox _user = new TextBox { Width = 200 };
        private readonly TextBox _token = new TextBox { Width = 200, UseSystemPasswordChar = true };

        public VaultSetupDialog(VaultInfo[] existing) : base("SwVault - Connect a Vault", 600, 330)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
            void Row(string label, Control control)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                grid.Controls.Add(control);
            }
            Row("Repository URL", _url);
            Row("Vault folder", _root);
            Row("User name", _user);
            Row("Access token", _token);
            grid.Controls.Add(new Label { Text = "", AutoSize = true });
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                ForeColor = Color.DimGray,
                Text = "Leave the folder empty to use the vault's shared folder. Create an access token in your server profile (Settings > Applications). "
                    + (existing.Length > 0 ? "Connected: " + string.Join(", ", existing.Select(v => v.Name)) : ""),
            });
            Controls.Add(grid);
            grid.BringToFront();
            OkButton.Text = "Connect";
        }

        public VaultAddRequest Request => new VaultAddRequest
        {
            RemoteUrl = _url.Text.Trim(),
            LocalRoot = _root.Text.Trim(),
            UserName = _user.Text.Trim(),
            Token = _token.Text.Trim(),
        };
    }

    /// <summary>Sign in to the team vault this install was packaged for: user name and password only.</summary>
    internal sealed class TeamSignInDialog : VaultDialog
    {
        private readonly TextBox _user = new TextBox { Width = 240 };
        private readonly TextBox _password = new TextBox { Width = 240, UseSystemPasswordChar = true };
        private readonly TeamInfo _team;

        public TeamSignInDialog(TeamInfo team) : base("SwVault - Sign in to " + team.Name, 460, 250)
        {
            _team = team;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(410, 0),
                Margin = new Padding(0, 0, 0, 10),
                Text = "Sign in with the user name and password from your team admin. SwVault then downloads the " + team.Name + " files to " + team.LocalRoot + ".",
            };
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 2);
            grid.Controls.Add(new Label { Text = "User name", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
            grid.Controls.Add(_user, 1, 1);
            grid.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 2);
            grid.Controls.Add(_password, 1, 2);
            Controls.Add(grid);
            grid.BringToFront();
            OkButton.Text = "Sign in";
            OkButton.Click += (s, e) =>
            {
                if (_user.Text.Trim().Length == 0 || _password.Text.Length == 0)
                {
                    DialogResult = DialogResult.None;
                    MessageBox.Show(this, "Enter your user name and password.", "SwVault");
                }
            };
        }

        public VaultAddRequest Request => new VaultAddRequest
        {
            RemoteUrl = _team.VaultUrl,
            UserName = _user.Text.Trim(),
            Password = _password.Text,
        };
    }

    /// <summary>Small progress window for vault jobs; stays on top of SOLIDWORKS but doesn't block it.</summary>
    internal sealed class ProgressWindow : Form
    {
        private readonly Label _message = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 12, 10, 0), AutoEllipsis = true };
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Marquee, Margin = new Padding(10) };

        public ProgressWindow(string title)
        {
            Text = "SwVault - " + title;
            Width = 420;
            Height = 130;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ControlBox = false;
            Font = SystemFonts.MessageBoxFont;
            var pad = new Panel { Dock = DockStyle.Top, Height = 10 };
            Controls.Add(pad);
            Controls.Add(_bar);
            Controls.Add(_message);
            _message.Text = title + "...";
        }

        public void Update(JobInfo job)
        {
            if (IsDisposed) return;
            if (!string.IsNullOrEmpty(job.Message)) _message.Text = job.Message;
            if (job.Progress > 0 && job.Progress <= 1)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = (int)(job.Progress * 100);
            }
        }

        public static ProgressWindow ShowFor(string title)
        {
            var window = new ProgressWindow(title);
            if (UiThread.Owner != null)
            {
                window.StartPosition = FormStartPosition.Manual;
                var screen = Screen.FromHandle(UiThread.Owner.Handle).WorkingArea;
                window.Location = new Point(screen.Left + (screen.Width - window.Width) / 2, screen.Top + (screen.Height - window.Height) / 2);
                window.Show(UiThread.Owner);
            }
            else
            {
                window.Show();
            }
            return window;
        }
    }
}
