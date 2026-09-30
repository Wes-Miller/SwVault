using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwVault.AddIn.Infrastructure;
using SwVault.AddIn.Sw;
using SwVault.AddIn.Ui;
using SwVault.Protocol;

namespace SwVault.AddIn
{
    /// <summary>
    /// The PDM workflows as SOLIDWORKS users see them. Each one asks the agent to do the vault work
    /// and takes care of the documents open in this session (save before check-in, release before
    /// files are replaced, reload afterwards, keep the read-only state in sync).
    /// </summary>
    internal sealed class VaultCommands
    {
        private readonly SwDocs _docs;
        private readonly AgentConnection _agent;
        private readonly HashSet<string> _promptedDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ProgressWindow _progress;
        private string _progressTag;

        public VaultCommands(SwDocs docs, AgentConnection agent)
        {
            _docs = docs;
            _agent = agent;
            _agent.JobUpdated += job =>
            {
                if (_progress != null && job.Tag == _progressTag) _progress.Update(job);
            };
        }

        /// <summary>Raised after an operation so the task pane can refresh.</summary>
        public event Action<IEnumerable<string>> Changed;

        // ------------------------------------------------------------ job plumbing

        /// <summary>
        /// Runs a job end to end: shows progress, releases open documents the job will replace,
        /// applies, reloads, and syncs read-only flags. Throws AgentException on failure.
        /// </summary>
        private async Task<JobInfo> RunJobAsync(JobRequest request, string title)
        {
            request.Tag = Guid.NewGuid().ToString("N");
            _progressTag = request.Tag;
            _progress = ProgressWindow.ShowFor(title);
            JobInfo job;
            var released = new List<ReleasedDoc>();
            try
            {
                job = await _agent.StartJobAsync(request);
                if (job.State == JobState.ReadyToApply)
                {
                    var blocked = new List<string>();
                    released = _docs.Release(job.Replacements ?? new string[0], blocked);
                    job = await _agent.ApplyJobAsync(job.JobId, blocked.ToArray());
                    if (blocked.Count > 0)
                        UiThread.ShowInfo("These files are open with unsaved changes, so they were not replaced:\n\n" + string.Join("\n", blocked.Select(Path.GetFileName)));
                }
            }
            finally
            {
                if (released.Count > 0) _docs.Restore(released);
                _progress?.Close();
                _progress = null;
                _progressTag = null;
            }

            if (job.State == JobState.Failed || job.State == JobState.Cancelled)
                throw new AgentException(job.ErrorCode == 0 ? ErrorCodes.Internal : job.ErrorCode, job.Error ?? "The operation did not complete.");

            _docs.SyncReadOnlyState((job.Affected ?? new string[0]).Concat(request.Paths ?? new string[0]));
            if (job.Warnings != null && job.Warnings.Length > 0)
                UiThread.ShowInfo(string.Join("\n\n", job.Warnings));
            Changed?.Invoke(job.Affected ?? request.Paths ?? new string[0]);
            return job;
        }

        private static List<string> InVault(IEnumerable<FileStatusDto> statuses) => statuses.Select(s => s.LocalPath).ToList();

        // ------------------------------------------------------------ check out

        public async Task CheckOutAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var targets = paths.ToList();
            if (targets.Count == 1 && SwDocs.HasReferences(targets[0]))
            {
                var tree = await _agent.ReferencesAsync(targets[0]);
                if (tree?.Children != null && tree.Children.Length > 0)
                {
                    using (var dialog = new CheckOutDialog(tree))
                    {
                        if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                        targets = dialog.SelectedPaths;
                    }
                    if (targets.Count == 0) return;
                }
            }

            var dirtyOutdated = await DirtyAndOutdatedAsync(targets);
            if (dirtyOutdated.Count > 0 && !UiThread.Confirm(
                    "A newer version exists for:\n\n" + string.Join("\n", dirtyOutdated.Select(Path.GetFileName)) +
                    "\n\nChecking out gets the newer version, and your unsaved changes to these files will be lost. Continue?"))
                return;

            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = targets.ToArray() }, "Checking out");
        }

        private async Task<List<string>> DirtyAndOutdatedAsync(IEnumerable<string> paths)
        {
            var dirty = _docs.DirtyAmong(paths);
            if (dirty.Count == 0) return dirty;
            var statuses = await _agent.GetStatusAsync(dirty.ToArray());
            return statuses.Where(s => s.LocalState == LocalState.Outdated || s.LocalState == LocalState.Conflict).Select(s => s.LocalPath).ToList();
        }

        // ------------------------------------------------------------ check in

        /// <summary>Checks in the given files plus anything in their reference trees that you changed or added.</summary>
        public async Task CheckInAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var candidates = new List<string>();
            foreach (var path in paths)
            {
                if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase)) candidates.Add(path);
                foreach (var reference in _docs.References(path, traverse: true))
                    if (!candidates.Contains(reference, StringComparer.OrdinalIgnoreCase)) candidates.Add(reference);
            }

            var dirty = _docs.DirtyAmong(candidates);
            if (dirty.Count > 0)
            {
                if (!UiThread.Confirm("Save these files before checking in?\n\n" + string.Join("\n", dirty.Select(Path.GetFileName)))) return;
                var failed = _docs.SaveDirty(dirty);
                if (failed.Count > 0)
                {
                    UiThread.ShowError("These files could not be saved (are they checked out?):\n\n" + string.Join("\n", failed.Select(Path.GetFileName)));
                    return;
                }
            }

            var statuses = await _agent.GetStatusAsync(candidates.ToArray());
            var requested = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var outside = candidates.Where(c => !statuses.Any(s => string.Equals(s.LocalPath, c, StringComparison.OrdinalIgnoreCase))).ToList();
            var rows = statuses
                .Where(s => requested.Contains(s.LocalPath) || s.LocalState == LocalState.LocalOnly || s.LockState == LockState.MineHere)
                .Select(s => (Status: s, Checked: s.LockState == LockState.MineHere || s.LocalState == LocalState.LocalOnly))
                .ToList();
            if (rows.Count == 0)
            {
                UiThread.ShowInfo("Nothing to check in: these files are not checked out by you and have no new files.");
                return;
            }

            string comment;
            bool keep;
            List<string> selected;
            using (var dialog = new CheckInDialog(rows))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                comment = dialog.Comment;
                keep = dialog.KeepCheckedOut;
                selected = dialog.SelectedPaths;
            }
            if (outside.Any(p => SwDocs.IsSolidWorksFile(p)) && !UiThread.Confirm(
                    "Some referenced files are outside the vault folder, so teammates won't be able to open them:\n\n" +
                    string.Join("\n", outside.Take(10)) + "\n\nCheck in anyway?"))
                return;

            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = selected.ToArray(),
                Comment = comment,
                KeepCheckedOut = keep,
                Files = selected.Select(FileInfoFor).ToArray(),
            }, "Checking in");
        }

        // ------------------------------------------------------------ add to vault

        /// <summary>
        /// One-click add: puts the document in a vault folder if it isn't in one yet (asking only
        /// where), saves it, and checks it in together with any new files it references.
        /// </summary>
        public async Task AddToVaultAsync(IModelDoc2 doc)
        {
            if (doc == null) return;
            var vaults = await _agent.GetVaultsAsync();
            if (vaults.Length == 0)
            {
                UiThread.ShowInfo("Connect this PC to a vault first.");
                await SetupVaultAsync();
                vaults = await _agent.GetVaultsAsync();
                if (vaults.Length == 0) return;
            }

            var path = SwDocs.PathOf(doc);
            if (string.IsNullOrEmpty(path) || VaultOf(vaults, path) == null)
            {
                var target = await ChooseVaultLocationAsync(doc, vaults, path);
                if (target == null) return;
                if (!_docs.SaveAs(doc, target))
                    throw new AgentException(ErrorCodes.Internal, "Could not save " + Path.GetFileName(target) + " in the vault folder.");
                path = target;
            }

            var candidates = new List<string> { path };
            foreach (var reference in _docs.References(path, traverse: true))
                if (!candidates.Contains(reference, StringComparer.OrdinalIgnoreCase)) candidates.Add(reference);

            var failed = _docs.SaveDirty(_docs.DirtyAmong(candidates));
            if (failed.Count > 0)
            {
                UiThread.ShowError("These files could not be saved:\n\n" + string.Join("\n", failed.Select(Path.GetFileName)));
                return;
            }

            var statuses = await _agent.GetStatusAsync(candidates.ToArray());
            var own = statuses.FirstOrDefault(s => string.Equals(s.LocalPath, path, StringComparison.OrdinalIgnoreCase));
            if (own == null) throw new AgentException(ErrorCodes.BadRequest, Path.GetFileName(path) + " is not in a vault folder.");
            if (own.LocalState == LocalState.Ignored)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " matches the vault's ignore list, so it can't be added.");
                return;
            }
            if (own.LocalState != LocalState.LocalOnly)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " is already in the vault." +
                                  (own.LockState == LockState.MineHere ? " Use Check In to upload your changes." : ""));
                return;
            }

            var toAdd = statuses.Where(s => s.LocalState == LocalState.LocalOnly).Select(s => s.LocalPath).ToList();
            var outside = candidates.Where(c => !statuses.Any(s => string.Equals(s.LocalPath, c, StringComparison.OrdinalIgnoreCase))).ToList();
            if (outside.Any(p => SwDocs.IsSolidWorksFile(p)) && !UiThread.Confirm(
                    "Some referenced files are outside the vault folder, so teammates won't be able to open them:\n\n" +
                    string.Join("\n", outside.Take(10)) + "\n\nAdd to the vault anyway?"))
                return;

            var name = Path.GetFileName(path);
            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = toAdd.ToArray(),
                Comment = toAdd.Count == 1 ? "Added " + name : "Added " + name + " and " + (toAdd.Count - 1) + " new referenced file(s)",
                Files = toAdd.Select(FileInfoFor).ToArray(),
            }, "Adding to vault");

            ((IFrame)_docs.App.Frame()).SetStatusBarText("SwVault: added " + name + (toAdd.Count > 1 ? " and " + (toAdd.Count - 1) + " referenced file(s)" : "") + " to the vault. Check it out to edit it again.");
        }

        private static VaultInfo VaultOf(IEnumerable<VaultInfo> vaults, string path) =>
            vaults.FirstOrDefault(v => !string.IsNullOrEmpty(v.LocalRoot)
                && path.StartsWith(v.LocalRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        /// <summary>Asks where in a vault folder to save a document that isn't in one yet.</summary>
        private async Task<string> ChooseVaultLocationAsync(IModelDoc2 doc, VaultInfo[] vaults, string currentPath)
        {
            var extension = SwDocs.ExtensionFor(doc);
            var fileName = string.IsNullOrEmpty(currentPath) ? Path.GetFileNameWithoutExtension(doc.GetTitle() ?? "") + extension : Path.GetFileName(currentPath);
            var folder = vaults[0].LocalRoot;
            while (true)
            {
                string target;
                using (var dialog = new SaveFileDialog
                {
                    Title = "Add to Vault - choose a folder inside your vault",
                    InitialDirectory = folder,
                    FileName = fileName,
                    Filter = "SOLIDWORKS file (*" + extension + ")|*" + extension,
                    DefaultExt = extension,
                    AddExtension = true,
                    OverwritePrompt = false,
                })
                {
                    if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return null;
                    target = dialog.FileName;
                }
                folder = Path.GetDirectoryName(target);
                fileName = Path.GetFileName(target);

                if (VaultOf(vaults, target) == null)
                {
                    UiThread.ShowError("Choose a folder inside your vault: " + string.Join(", ", vaults.Select(v => v.LocalRoot)));
                    continue;
                }
                if (File.Exists(target))
                {
                    var existing = (await _agent.GetStatusAsync(target)).FirstOrDefault();
                    if (existing != null && existing.LocalState != LocalState.LocalOnly)
                    {
                        UiThread.ShowError(fileName + " is already in the vault. Choose a different name.");
                        continue;
                    }
                    if (!UiThread.Confirm(fileName + " already exists in that folder. Replace it?")) continue;
                }
                return target;
            }
        }

        /// <summary>What the vault stores about a file: references, custom properties, SOLIDWORKS version.</summary>
        private CheckInFileInfo FileInfoFor(string path)
        {
            var info = new CheckInFileInfo { LocalPath = path };
            if (!SwDocs.IsSolidWorksFile(path)) return info;
            info.References = _docs.References(path, traverse: false);
            info.SwVersion = _docs.Version();
            var doc = _docs.FindOpen(path);
            if (doc != null)
            {
                info.Properties = _docs.Properties(doc);
                info.Configurations = _docs.Configurations(doc);
            }
            return info;
        }

        // ------------------------------------------------------------ get latest / undo

        public Task GetLatestAsync(IReadOnlyList<string> paths, bool withReferences) =>
            paths.Count == 0 ? Task.CompletedTask
                : RunJobAsync(new JobRequest { Kind = JobKind.GetLatest, Paths = paths.ToArray(), WithReferences = withReferences }, "Getting latest");

        public async Task UndoCheckOutAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var statuses = await _agent.GetStatusAsync(paths.ToArray());
            var changed = statuses.Where(s => s.LocalState == LocalState.Modified || s.LocalState == LocalState.Conflict).ToList();
            var dirty = _docs.DirtyAmong(paths);
            if ((changed.Count > 0 || dirty.Count > 0) && !UiThread.Confirm(
                    "Undo check-out discards your changes to:\n\n" + string.Join("\n", changed.Select(s => s.LocalPath).Concat(dirty).Distinct(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName)) +
                    "\n\nContinue?"))
                return;

            // Unsaved edits must go too: reload from disk first so the documents can be released.
            foreach (var path in dirty)
                _docs.FindOpen(path)?.ReloadOrReplace(false, path, true);

            await RunJobAsync(new JobRequest { Kind = JobKind.UndoCheckOut, Paths = paths.ToArray(), Force = true }, "Undoing check-out");
        }

        // ------------------------------------------------------------ history

        public async Task HistoryAsync(string path)
        {
            var versions = await _agent.HistoryAsync(path);
            if (versions.Length == 0)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " has no history in the vault yet.");
                return;
            }
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            HistoryDialog.HistoryAction action;
            VersionInfoDto version;
            bool asBuilt;
            using (var dialog = new HistoryDialog(path, versions, status?.LockState == LockState.MineHere, SwDocs.HasReferences(path)))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK || dialog.SelectedVersion == null) return;
                action = dialog.Action;
                version = dialog.SelectedVersion;
                asBuilt = dialog.AsBuilt;
            }

            if (action == HistoryDialog.HistoryAction.GetVersion)
            {
                await RunJobAsync(new JobRequest { Kind = JobKind.GetVersion, Paths = new[] { path }, Version = version.Version, AsBuilt = asBuilt }, "Getting version " + version.Version);
                UiThread.ShowInfo(Path.GetFileName(path) + " is now at version " + version.Version + " (read-only). Use Get Latest to return to the newest version.");
            }
            else if (action == HistoryDialog.HistoryAction.Rollback)
            {
                await RunJobAsync(new JobRequest { Kind = JobKind.Rollback, Paths = new[] { path }, Version = version.Version }, "Rolling back");
                UiThread.ShowInfo("Version " + version.Version + " is in place. Check the file in to make it the newest version.");
            }
        }

        public async Task WhereUsedAsync(string path)
        {
            var parents = await _agent.WhereUsedAsync(path);
            if (parents.Length == 0)
            {
                UiThread.ShowInfo("No files in the vault reference " + Path.GetFileName(path) + ".");
                return;
            }
            using (var dialog = new FileListDialog("Where Used - " + Path.GetFileName(path), "Files that reference " + Path.GetFileName(path) + ":", parents))
            {
                if (dialog.ShowDialog(UiThread.Owner) == DialogResult.OK && dialog.SelectedFile != null)
                    await OpenAsync(dialog.SelectedFile);
            }
        }

        // ------------------------------------------------------------ workflow

        public async Task ChangeStateAsync(string path)
        {
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            if (status == null || status.ServerVersion == 0)
            {
                UiThread.ShowInfo("Check the file in before changing its workflow state.");
                return;
            }
            var options = await _agent.TransitionsAsync(path);
            TransitionOptionDto option;
            string comment;
            using (var dialog = new TransitionDialog(path, status.State, status.Revision, options))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK || dialog.Selected == null) return;
                option = dialog.Selected;
                comment = dialog.Comment;
            }

            if (option.BumpRevision && SwDocs.IsSolidWorksFile(path))
                await ReleaseWithRevisionAsync(path, option, comment);
            else
                await RunJobAsync(new JobRequest { Kind = JobKind.Transition, Paths = new[] { path }, TransitionName = option.Name, Comment = comment }, option.Name);
        }

        /// <summary>
        /// Release: check out for the transition, stamp Revision/ReleasedBy/ReleaseDate into the file,
        /// export PDF/STEP, and check everything in with the new state in one commit.
        /// </summary>
        private async Task ReleaseWithRevisionAsync(string path, TransitionOptionDto option, string comment)
        {
            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = new[] { path }, TransitionName = option.Name }, "Preparing release");

            var vaults = await _agent.GetVaultsAsync();
            var user = vaults.FirstOrDefault(v => path.StartsWith(v.LocalRoot, StringComparison.OrdinalIgnoreCase))?.UserDisplayName ?? System.Environment.UserName;
            var doc = _docs.FindOpen(path);
            var openedHere = doc == null;
            if (doc == null) doc = _docs.Open(path, silent: true);
            if (doc == null) throw new AgentException(ErrorCodes.Internal, "Could not open " + Path.GetFileName(path) + " to stamp the revision.");

            var exports = new List<string>();
            try
            {
                _docs.SetProperty(doc, "Revision", option.NextRevision);
                _docs.SetProperty(doc, "ReleasedBy", user);
                _docs.SetProperty(doc, "ReleaseDate", DateTime.Now.ToString("yyyy-MM-dd"));
                doc.ForceRebuild3(false);
                if (!_docs.Save(doc)) throw new AgentException(ErrorCodes.Internal, "Could not save " + Path.GetFileName(path) + ".");

                var type = doc.GetType();
                var name = Path.GetFileNameWithoutExtension(path) + "_Rev" + option.NextRevision;
                foreach (var kind in option.Exports ?? new string[0])
                {
                    string extension = null;
                    if (kind == "pdf" && type == (int)swDocumentTypes_e.swDocDRAWING) extension = ".pdf";
                    else if (kind == "dxf" && type == (int)swDocumentTypes_e.swDocDRAWING) extension = ".dxf";
                    else if (kind == "step" && type != (int)swDocumentTypes_e.swDocDRAWING) extension = ".step";
                    if (extension == null) continue;
                    var target = Path.Combine(option.ExportFolder ?? Path.GetDirectoryName(path), name + extension);
                    if (_docs.Export(doc, target)) exports.Add(target);
                }
            }
            finally
            {
                if (openedHere) _docs.Close(doc);
            }

            var files = new List<CheckInFileInfo> { FileInfoFor(path) };
            await RunJobAsync(new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = new[] { path }.Concat(exports).ToArray(),
                TransitionName = option.Name,
                Comment = string.IsNullOrEmpty(comment) ? "Released revision " + option.NextRevision : comment,
                Files = files.ToArray(),
            }, "Releasing revision " + option.NextRevision);
        }

        // ------------------------------------------------------------ open & prompts

        /// <summary>Opens a vault file, first offering to get the latest versions of anything it references.</summary>
        public async Task OpenAsync(string path)
        {
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            var needsFiles = status == null || status.LocalState == LocalState.NotLocal || status.LocalState == LocalState.MissingLocally;
            if (SwDocs.HasReferences(path) || needsFiles)
            {
                var tree = await _agent.ReferencesAsync(path);
                var stale = Flatten(tree).Where(n => n.Status != null && (n.Status.LocalState == LocalState.NotLocal
                    || n.Status.LocalState == LocalState.MissingLocally || n.Status.LocalState == LocalState.Outdated)).ToList();
                var missing = stale.Any(n => n.Status.LocalState != LocalState.Outdated);
                if (stale.Count > 0 && (missing || UiThread.Confirm(stale.Count + " file(s) have newer versions in the vault. Get the latest before opening?")))
                    await GetLatestAsync(new[] { path }, withReferences: true);
            }
            if (!File.Exists(path)) throw new AgentException(ErrorCodes.NotFound, Path.GetFileName(path) + " is not available locally.");
            _docs.Open(path, silent: false);
        }

        private static IEnumerable<ReferenceNodeDto> Flatten(ReferenceNodeDto node)
        {
            if (node == null) yield break;
            yield return node;
            foreach (var child in node.Children ?? new ReferenceNodeDto[0])
                foreach (var n in Flatten(child)) yield return n;
        }

        /// <summary>First edit of a read-only vault document: offer to check it out.</summary>
        public async Task OnDocumentModifiedAsync(IModelDoc2 doc)
        {
            var path = SwDocs.PathOf(doc);
            if (string.IsNullOrEmpty(path) || _promptedDocs.Contains(path) || !doc.IsOpenedReadOnly()) return;
            var status = (await _agent.GetStatusAsync(path)).FirstOrDefault();
            if (status == null || status.LocalState == LocalState.LocalOnly || status.LockState == LockState.MineHere) return;
            _promptedDocs.Add(path);

            string question;
            if (status.LockState == LockState.Other)
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " is checked out by " + status.LockOwner + ". You can look and experiment, but you won't be able to save.");
                return;
            }
            if (!string.IsNullOrEmpty(status.State) && (status.State == "Released" || status.State == "InReview" || status.State == "Obsolete"))
            {
                UiThread.ShowInfo(Path.GetFileName(path) + " is " + status.State + ". Use Change State > Change request before editing it.");
                return;
            }
            question = status.LocalState == LocalState.Outdated
                ? Path.GetFileName(path) + " is not checked out, and a newer version exists. Check out now? (The newer version replaces the file and your edits so far are discarded.)"
                : Path.GetFileName(path) + " is not checked out, so you won't be able to save it. Check it out now?";
            if (!UiThread.Confirm(question)) return;
            if (status.LocalState == LocalState.Outdated) doc.ReloadOrReplace(true, path, true);
            await RunJobAsync(new JobRequest { Kind = JobKind.CheckOut, Paths = new[] { path } }, "Checking out");
        }

        public void OnDocumentClosed(string path)
        {
            if (!string.IsNullOrEmpty(path)) _promptedDocs.Remove(path);
        }

        // ------------------------------------------------------------ setup

        public async Task SetupVaultAsync()
        {
            var existing = await _agent.GetVaultsAsync();
            using (var dialog = new VaultSetupDialog(existing))
            {
                if (dialog.ShowDialog(UiThread.Owner) != DialogResult.OK) return;
                var progress = ProgressWindow.ShowFor("Connecting");
                try
                {
                    var vault = await _agent.AddVaultAsync(dialog.Request);
                    UiThread.ShowInfo("Connected to " + vault.Name + " as " + vault.UserLogin + ".\nVault folder: " + vault.LocalRoot);
                }
                finally
                {
                    progress.Close();
                }
            }
            Changed?.Invoke(new string[0]);
        }
    }
}
