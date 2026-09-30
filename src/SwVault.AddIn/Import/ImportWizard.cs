using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.AddIn.Infrastructure;
using SwVault.AddIn.Sw;
using SwVault.Protocol;

namespace SwVault.AddIn.Import
{
    /// <summary>What an import will do, and what might go wrong, before anything is copied.</summary>
    internal sealed class ImportPlan
    {
        public string Source { get; set; }
        public string Destination { get; set; }
        public List<KeyValuePair<string, string>> Files { get; } = new List<KeyValuePair<string, string>>();
        public List<string> Missing { get; } = new List<string>();
        public List<string> Outside { get; } = new List<string>();
        public List<string> DuplicateNames { get; } = new List<string>();
        public List<string> LongPaths { get; } = new List<string>();
        public List<string> AlreadyThere { get; } = new List<string>();

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Files.Count + " files will be copied to " + Destination + ".");
            if (AlreadyThere.Count > 0) sb.AppendLine(AlreadyThere.Count + " already exist there and will be skipped.");
            void Section(string title, List<string> items)
            {
                if (items.Count == 0) return;
                sb.AppendLine().AppendLine(title + " (" + items.Count + "):");
                foreach (var i in items.Take(40)) sb.AppendLine("  " + i);
                if (items.Count > 40) sb.AppendLine("  ...");
            }
            Section("Referenced files that can't be found (those references will stay broken)", Missing);
            Section("Referenced files outside the source folder (teammates won't have them)", Outside);
            Section("File names used more than once (SOLIDWORKS can mix these up in assemblies)", DuplicateNames);
            Section("Paths longer than 250 characters", LongPaths);
            if (Missing.Count + Outside.Count + DuplicateNames.Count + LongPaths.Count == 0) sb.AppendLine().AppendLine("No problems found.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Imports an existing folder of SOLIDWORKS files: copy into the vault, re-point assembly and
    /// drawing references at the copies (ReplaceReferencedDocument works on closed files), then
    /// check everything in as batched import commits.
    /// </summary>
    internal sealed class ImportWizard : Form
    {
        private static readonly string[] Skip = { "~$", ".swbak", ".bak", "thumbs.db", "desktop.ini" };
        private readonly SwDocs _docs;
        private readonly AgentConnection _agent;
        private readonly TextBox _source = new TextBox { Width = 420 };
        private readonly TextBox _destination = new TextBox { Width = 420 };
        private readonly TextBox _report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9) };
        private readonly TextBox _comment = new TextBox { Width = 420, Text = "Imported existing files" };
        private readonly Button _scan = new Button { Text = "Scan", AutoSize = true };
        private readonly Button _import = new Button { Text = "Import", AutoSize = true, Enabled = false };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private ImportPlan _plan;

        public ImportWizard(SwDocs docs, AgentConnection agent)
        {
            _docs = docs;
            _agent = agent;
            Text = "SwVault - Import Folder";
            Width = 780;
            Height = 580;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
            void Row(string label, Control box, Action browse)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
                grid.Controls.Add(box);
                if (browse == null)
                {
                    grid.Controls.Add(new Label());
                    return;
                }
                var button = new Button { Text = "Browse...", AutoSize = true };
                button.Click += (s, e) => browse();
                grid.Controls.Add(button);
            }
            Row("Existing folder", _source, () => Browse(_source, "Folder with the existing SOLIDWORKS files"));
            Row("Vault folder", _destination, () => Browse(_destination, "Destination folder inside your vault"));
            Row("Comment", _comment, null);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(close);
            buttons.Controls.Add(_import);
            buttons.Controls.Add(_scan);
            buttons.Controls.Add(_status);

            Controls.Add(_report);
            Controls.Add(buttons);
            Controls.Add(grid);
            _report.BringToFront();
            CancelButton = close;
            _report.Text = "1. Pick the folder with your existing files (e.g. a network share).\r\n2. Pick a folder inside your vault to copy them into.\r\n3. Scan to see what will happen, then Import.\r\n\r\nClose SOLIDWORKS documents from the source folder before importing.";
            _scan.Click += (s, e) => UiThread.Run("Import scan", ScanAsync);
            _import.Click += (s, e) => UiThread.Run("Import", ImportAsync);
            _source.TextChanged += (s, e) => _import.Enabled = false;
            _destination.TextChanged += (s, e) => _import.Enabled = false;
        }

        private void Browse(TextBox target, string description)
        {
            using (var dialog = new FolderBrowserDialog { Description = description, SelectedPath = target.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }

        private async Task ScanAsync()
        {
            var source = _source.Text.Trim().TrimEnd('\\');
            var destination = _destination.Text.Trim().TrimEnd('\\');
            if (!Directory.Exists(source)) throw new AgentException(ErrorCodes.BadRequest, "The existing folder doesn't exist.");
            var vaults = await _agent.GetVaultsAsync();
            var vault = vaults.FirstOrDefault(v => destination.StartsWith(v.LocalRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (vault == null) throw new AgentException(ErrorCodes.BadRequest, "The vault folder must be inside one of your vaults: " + string.Join(", ", vaults.Select(v => v.LocalRoot)));
            if (destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase) || source.StartsWith(destination + "\\", StringComparison.OrdinalIgnoreCase))
                throw new AgentException(ErrorCodes.BadRequest, "The existing folder and the vault folder must not contain each other.");

            _status.Text = "Scanning...";
            _scan.Enabled = false;
            try
            {
                _plan = Plan(source, destination);
                _report.Text = _plan.Report().Replace("\n", "\r\n");
                _import.Enabled = _plan.Files.Count > 0;
            }
            finally
            {
                _status.Text = "";
                _scan.Enabled = true;
            }
        }

        private ImportPlan Plan(string source, string destination)
        {
            var plan = new ImportPlan { Source = source, Destination = destination };
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => !Skip.Any(s => Path.GetFileName(f).StartsWith(s, StringComparison.OrdinalIgnoreCase) || f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var from in files)
            {
                var to = Path.Combine(destination, from.Substring(source.Length).TrimStart('\\'));
                if (File.Exists(to)) plan.AlreadyThere.Add(to);
                else plan.Files.Add(new KeyValuePair<string, string>(from, to));
                if (to.Length > 250) plan.LongPaths.Add(to);
            }

            plan.DuplicateNames.AddRange(files.Where(SwDocs.IsSolidWorksFile)
                .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key + ": " + string.Join(", ", g.Select(f => f.Substring(source.Length)))));

            var sourceSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(f => SwDocs.IsSolidWorksFile(f)))
            {
                _status.Text = "Scanning " + Path.GetFileName(file);
                Application.DoEvents();
                foreach (var reference in _docs.References(file, traverse: false))
                {
                    if (sourceSet.Contains(reference)) continue;
                    var line = Path.GetFileName(file) + " -> " + reference;
                    if (File.Exists(reference)) plan.Outside.Add(line);
                    else plan.Missing.Add(line);
                }
            }
            return plan;
        }

        private async Task ImportAsync()
        {
            if (_plan == null) return;
            if (!UiThread.Confirm("Copy " + _plan.Files.Count + " files into " + _plan.Destination + " and check them in?")) return;
            _import.Enabled = false;
            _scan.Enabled = false;
            try
            {
                var copied = Copy();
                var fixedCount = Repoint(copied);
                _status.Text = "Checking in...";
                var infos = copied.Select(c => new CheckInFileInfo
                {
                    LocalPath = c.Value,
                    References = SwDocs.IsSolidWorksFile(c.Value) ? _docs.References(c.Value, traverse: false) : null,
                    SwVersion = SwDocs.IsSolidWorksFile(c.Value) ? _docs.Version() : null,
                }).ToArray();
                var job = await _agent.StartJobAsync(new JobRequest
                {
                    Kind = JobKind.Import,
                    Paths = copied.Select(c => c.Value).ToArray(),
                    Files = infos,
                    Comment = _comment.Text.Trim(),
                    AutoApply = true,
                });
                if (job.State == JobState.Failed) throw new AgentException(job.ErrorCode, job.Error);
                _report.Text = "Imported " + copied.Count + " files; re-pointed " + fixedCount + " references.\r\n\r\n" +
                               string.Join("\r\n", job.Warnings ?? new string[0]);
                _status.Text = "Done.";
            }
            finally
            {
                _scan.Enabled = true;
            }
        }

        private List<KeyValuePair<string, string>> Copy()
        {
            var copied = new List<KeyValuePair<string, string>>();
            var i = 0;
            foreach (var pair in _plan.Files)
            {
                if (++i % 20 == 0)
                {
                    _status.Text = "Copying " + i + "/" + _plan.Files.Count;
                    Application.DoEvents();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Value));
                File.Copy(pair.Key, pair.Value, overwrite: false);
                File.SetAttributes(pair.Value, File.GetAttributes(pair.Value) & ~FileAttributes.ReadOnly);
                copied.Add(pair);
            }
            return copied;
        }

        /// <summary>Points references in the copies at the other copies (by relative path, else by unique file name).</summary>
        private int Repoint(List<KeyValuePair<string, string>> copied)
        {
            var byOldPath = copied.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
            var byName = copied.Where(c => SwDocs.IsSolidWorksFile(c.Value))
                .GroupBy(c => Path.GetFileName(c.Value), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var i = 0;
            foreach (var copy in copied.Where(c => SwDocs.IsSolidWorksFile(c.Value)))
            {
                if (++i % 10 == 0)
                {
                    _status.Text = "Fixing references " + i;
                    Application.DoEvents();
                }
                foreach (var stored in StoredReferences(copy.Value))
                {
                    string target;
                    if (!byOldPath.TryGetValue(stored, out target) && !byName.TryGetValue(Path.GetFileName(stored), out target)) continue;
                    if (string.Equals(stored, target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_docs.App.ReplaceReferencedDocument(copy.Value, stored, target)) count++;
                    else Log.Warn("ReplaceReferencedDocument failed: " + copy.Value + " : " + stored + " -> " + target);
                }
            }
            return count;
        }

        /// <summary>Reference paths exactly as stored in the file (no search rules).</summary>
        private IEnumerable<string> StoredReferences(string path)
        {
            var raw = _docs.App.GetDocumentDependencies2(path, false, false, false);
            var values = raw as string[] ?? (raw as object[])?.Cast<string>().ToArray() ?? new string[0];
            for (var i = 1; i < values.Length; i += 2)
                if (!string.IsNullOrEmpty(values[i])) yield return values[i];
        }
    }
}
