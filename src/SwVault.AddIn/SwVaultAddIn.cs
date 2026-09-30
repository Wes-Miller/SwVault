using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SwVault.AddIn.Import;
using SwVault.AddIn.Infrastructure;
using SwVault.AddIn.Sw;
using SwVault.AddIn.Ui;
using SwVault.Protocol;

namespace SwVault.AddIn
{
    /// <summary>
    /// SOLIDWORKS add-in entry point. Registered as a COM class (RegAsm /codebase) plus the
    /// SOLIDWORKS add-in registry keys written by <see cref="RegisterFunction"/>.
    /// </summary>
    [ComVisible(true)]
    [Guid(ProtocolInfo.AddInGuid)]
    [ProgId("SwVault.AddIn")]
    public sealed class SwVaultAddIn : ISwAddin
    {
        private const int CommandGroupId = 7310;
        private const string Title = "SwVault";

        private ISldWorks _sw;
        private SldWorks _swEvents;
        private int _cookie;
        private ICommandManager _commandManager;
        private ITaskpaneView _taskPaneView;
        private VaultPane _pane;
        private AgentConnection _agent;
        private SwDocs _docs;
        private DocEvents _docEvents;
        private VaultCommands _commands;
        private readonly List<int> _commandIndexes = new List<int>();
        private DSldWorksEvents_ActiveDocChangeNotifyEventHandler _onActiveDocChange;
        private DSldWorksEvents_FileOpenPostNotifyEventHandler _onFileOpenPost;
        private DSldWorksEvents_DocumentLoadNotify2EventHandler _onDocumentLoad;

        private sealed class CommandSpec
        {
            public string Name;
            public string Hint;
            public int Glyph; // Segoe MDL2 Assets code point
            public Color Color;
            public string Callback;
            public string Enable;
        }

        private static readonly CommandSpec[] Commands =
        {
            new CommandSpec { Name = "Add to Vault", Hint = "Put this file in the vault in one step: saves it into your vault folder if needed and checks it in", Glyph = 0xE710, Color = Color.FromArgb(0, 150, 136), Callback = nameof(OnAddToVault), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Check Out", Hint = "Lock the file(s) for editing so nobody else changes them", Glyph = 0xE785, Color = Color.FromArgb(0, 102, 204), Callback = nameof(OnCheckOut), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Check In", Hint = "Upload your changes as a new version and release the lock", Glyph = 0xE898, Color = Color.FromArgb(46, 139, 87), Callback = nameof(OnCheckIn), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Undo Check Out", Hint = "Discard your changes and release the lock", Glyph = 0xE7A7, Color = Color.FromArgb(160, 80, 60), Callback = nameof(OnUndoCheckOut), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Get Latest", Hint = "Download the newest versions (including referenced files)", Glyph = 0xE896, Color = Color.FromArgb(230, 145, 0), Callback = nameof(OnGetLatest), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "History", Hint = "Versions, comments, get an older version or roll back", Glyph = 0xE81C, Color = Color.FromArgb(90, 90, 160), Callback = nameof(OnHistory), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Where Used", Hint = "Assemblies and drawings that use this file", Glyph = 0xE71B, Color = Color.FromArgb(90, 120, 140), Callback = nameof(OnWhereUsed), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Change State", Hint = "Submit for review, approve/release, change request", Glyph = 0xE7C1, Color = Color.FromArgb(128, 64, 160), Callback = nameof(OnChangeState), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Request Review", Hint = "Ask a subteam lead for a design, simulation or drawing review of this file", Glyph = 0xE8F2, Color = Color.FromArgb(180, 90, 0), Callback = nameof(OnRequestReview), Enable = nameof(EnableWithDocument) },
            new CommandSpec { Name = "Reviews", Hint = "Review requests for you (as a lead) and the ones you sent", Glyph = 0xE8BD, Color = Color.FromArgb(180, 90, 0), Callback = nameof(OnReviews), Enable = nameof(EnableAlways) },
            new CommandSpec { Name = "Subsystems", Hint = "Cars and subsystems, their responsible engineers; add a subsystem or become its RE", Glyph = 0xE8FD, Color = Color.FromArgb(0, 102, 204), Callback = nameof(OnSubsystems), Enable = nameof(EnableAlways) },
            new CommandSpec { Name = "Import Folder", Hint = "Copy an existing folder of SOLIDWORKS files into the vault, fixing references", Glyph = 0xE8B5, Color = Color.FromArgb(70, 110, 70), Callback = nameof(OnImport), Enable = nameof(EnableAlways) },
            new CommandSpec { Name = "Refresh", Hint = "Check the server for new versions and check-outs", Glyph = 0xE72C, Color = Color.FromArgb(100, 100, 100), Callback = nameof(OnRefresh), Enable = nameof(EnableAlways) },
            new CommandSpec { Name = "Invite People", Hint = "Vault admins: make an invite link to send to new team members", Glyph = 0xE8FA, Color = Color.FromArgb(0, 120, 212), Callback = nameof(OnInvite), Enable = nameof(EnableAlways) },
            new CommandSpec { Name = "Vaults", Hint = "Connect this PC to a vault", Glyph = 0xE713, Color = Color.FromArgb(100, 100, 100), Callback = nameof(OnSettings), Enable = nameof(EnableAlways) },
        };

        private static readonly (string Name, string Callback)[] PopupItems =
        {
            ("Check Out@SwVault", nameof(OnCheckOutSelected)),
            ("Check In@SwVault", nameof(OnCheckInSelected)),
            ("Get Latest@SwVault", nameof(OnGetLatestSelected)),
            ("History@SwVault", nameof(OnHistorySelected)),
        };

        // ------------------------------------------------------------ COM registration

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            var guid = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (var addin = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                addin.SetValue(null, 1, RegistryValueKind.DWord);
                addin.SetValue("Title", Title);
                addin.SetValue("Description", "Check out, check in and share SOLIDWORKS files through a Git LFS vault.");
            }
            using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
            {
                startup.SetValue(null, 1, RegistryValueKind.DWord);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            var guid = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\Addins\" + guid, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + guid, false);
        }

        // ------------------------------------------------------------ ISwAddin

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                _sw = (ISldWorks)ThisSW;
                _cookie = Cookie;
                _sw.SetAddinCallbackInfo2(0, this, _cookie);
                UiThread.Initialize();
                UiThread.Owner = new WindowHandle(new IntPtr(((IFrame)_sw.Frame()).GetHWndx64()));
                Log.Info("SwVault add-in loading in SOLIDWORKS " + _sw.RevisionNumber());

                _agent = new AgentConnection();
                _docs = new SwDocs(_sw);
                _commands = new VaultCommands(_docs, _agent);
                _docEvents = new DocEvents();
                _docEvents.Modified += doc => UiThread.Run("Check-out prompt", () => _commands.OnDocumentModifiedAsync(doc));
                _docEvents.Saved += (doc, name) => RefreshActive();
                _docEvents.Closed += path => _commands.OnDocumentClosed(path);
                _agent.Toast += toast => ((IFrame)_sw.Frame()).SetStatusBarText("SwVault: " + toast.Title + " - " + toast.Message.Replace("\n", "; "));

                CreateCommands();
                CreateTaskPane();
                HookEvents();
                foreach (var doc in _docs.LoadedDocuments()) _docEvents.Attach(doc);

                // Connect (and start the agent if needed) without blocking SOLIDWORKS start-up.
                UiThread.Post(() => UiThread.Run("Connect to agent", async () =>
                {
                    await _agent.EnsureConnectedAsync();
                    await _pane.RefreshAllAsync(sync: false);
                    RefreshActive();
                }));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("ConnectToSW failed", ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                UnhookEvents();
                _docEvents?.Dispose();
                RemoveCommands();
                _taskPaneView?.DeleteView();
                _pane?.Dispose();
                _agent?.Dispose();
                UiThread.Dispose();
                Log.Info("SwVault add-in unloaded");
            }
            catch (Exception ex)
            {
                Log.Error("DisconnectFromSW failed", ex);
            }
            finally
            {
                if (_taskPaneView != null) Marshal.ReleaseComObject(_taskPaneView);
                if (_commandManager != null) Marshal.ReleaseComObject(_commandManager);
                _taskPaneView = null;
                _commandManager = null;
                _sw = null;
                _swEvents = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return true;
        }

        // ------------------------------------------------------------ command manager

        private void CreateCommands()
        {
            _commandManager = _sw.GetCommandManager(_cookie);
            var userIds = Enumerable.Range(1, Commands.Length).ToArray();
            object registryIds;
            var hadData = _commandManager.GetGroupDataFromRegistry(CommandGroupId, out registryIds);
            var ignorePrevious = hadData && !(registryIds is int[] ids && ids.SequenceEqual(userIds));

            var errors = 0;
            var group = _commandManager.CreateCommandGroup2(CommandGroupId, Title, "SwVault PDM", "Check out, check in and share files", -1, ignorePrevious, ref errors);
            var glyphs = Commands.Select(c => new Icons.Glyph((char)c.Glyph, c.Color)).ToList();
            group.IconList = Icons.CommandStrips(glyphs, "commands");
            group.MainIconList = Icons.CommandStrips(new[] { new Icons.Glyph((char)0xE72E, Color.FromArgb(0, 102, 204)) }, "main");

            var menuAndToolbar = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;
            _commandIndexes.Clear();
            for (var i = 0; i < Commands.Length; i++)
            {
                var c = Commands[i];
                _commandIndexes.Add(group.AddCommandItem2(c.Name, -1, c.Hint, c.Name, i, c.Callback, c.Enable, userIds[i], menuAndToolbar));
            }
            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();

            var commandIds = _commandIndexes.Select(index => group.get_CommandID(index)).ToArray();
            var textStyles = commandIds.Select(_ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
            foreach (var docType in new[] { swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY, swDocumentTypes_e.swDocDRAWING })
            {
                var tab = _commandManager.GetCommandTab((int)docType, Title);
                if (tab != null && (!hadData || ignorePrevious))
                {
                    _commandManager.RemoveCommandTab(tab);
                    tab = null;
                }
                if (tab == null)
                {
                    tab = _commandManager.AddCommandTab((int)docType, Title);
                    tab.AddCommandTabBox().AddCommands(commandIds, textStyles);
                }
            }

            foreach (var (name, callback) in PopupItems)
                _sw.AddMenuPopupItem3((int)swDocumentTypes_e.swDocASSEMBLY, _cookie, (int)swSelectType_e.swSelCOMPONENTS, name, callback, nameof(EnableAlways), name.Split('@')[0], "");
        }

        private void RemoveCommands()
        {
            if (_sw == null) return;
            foreach (var (name, callback) in PopupItems)
                _sw.RemoveMenuPopupItem2((int)swDocumentTypes_e.swDocASSEMBLY, _cookie, (int)swSelectType_e.swSelCOMPONENTS, name, callback, nameof(EnableAlways), name.Split('@')[0], "");
            _commandManager?.RemoveCommandGroup2(CommandGroupId, true);
        }

        private void CreateTaskPane()
        {
            _taskPaneView = _sw.CreateTaskpaneView2(Icons.TaskPaneIcon(), "SwVault");
            _pane = new VaultPane(_agent, _commands);
            _pane.AddToVaultRequested += OnAddToVault;
            _pane.CreateControl();
            _taskPaneView.DisplayWindowFromHandlex64(_pane.Handle.ToInt64());
        }

        // ------------------------------------------------------------ SOLIDWORKS events

        private void HookEvents()
        {
            _swEvents = (SldWorks)_sw;
            _onActiveDocChange = () => { RefreshActive(); return 0; };
            _onFileOpenPost = fileName => { UiThread.Post(() => OnFileOpened(fileName)); return 0; };
            _onDocumentLoad = (title, path) => { UiThread.Post(() => _docEvents.Attach(_docs.FindOpen(path))); return 0; };
            _swEvents.ActiveDocChangeNotify += _onActiveDocChange;
            _swEvents.FileOpenPostNotify += _onFileOpenPost;
            _swEvents.DocumentLoadNotify2 += _onDocumentLoad;
        }

        private void UnhookEvents()
        {
            if (_swEvents == null) return;
            _swEvents.ActiveDocChangeNotify -= _onActiveDocChange;
            _swEvents.FileOpenPostNotify -= _onFileOpenPost;
            _swEvents.DocumentLoadNotify2 -= _onDocumentLoad;
        }

        private void RefreshActive()
        {
            var path = ActivePath();
            UiThread.Post(() => UiThread.Run("Status", () => _pane.ShowActiveDocAsync(path)));
        }

        /// <summary>Path of the active document: null when none is open, "" when it has never been saved.</summary>
        private string ActivePath()
        {
            var doc = _docs?.ActiveDoc;
            return doc == null ? null : SwDocs.PathOf(doc);
        }

        /// <summary>After opening a vault file, point out when a newer version exists.</summary>
        private void OnFileOpened(string fileName)
        {
            var doc = _docs.FindOpen(fileName);
            if (doc != null) _docEvents.Attach(doc);
            UiThread.Run("Open check", async () =>
            {
                if (!_agent.IsConnected) return;
                var status = (await _agent.GetStatusAsync(fileName)).FirstOrDefault();
                if (status == null) return;
                if (status.LocalState == LocalState.Outdated)
                    ((IFrame)_sw.Frame()).SetStatusBarText("SwVault: a newer version of " + System.IO.Path.GetFileName(fileName) + " exists (v" + status.ServerVersion + ") - use Get Latest.");
                await _pane.ShowActiveDocAsync(ActivePath());
            });
        }

        // ------------------------------------------------------------ command callbacks (called by SOLIDWORKS)

        /// <summary>Files a command applies to: selected components in an assembly, else the active document.</summary>
        private List<string> Targets()
        {
            var selected = _docs.SelectedComponentPaths();
            if (selected.Count > 0) return selected;
            var path = SwDocs.PathOf(_docs.ActiveDoc);
            if (string.IsNullOrEmpty(path))
            {
                UiThread.ShowInfo("Save the document inside your vault folder first.");
                return new List<string>();
            }
            return new List<string> { path };
        }

        private static void Run(string what, Func<Task> body) => UiThread.Run(what, body);

        public void OnAddToVault() => Run("Add to vault", async () =>
        {
            try
            {
                await _commands.AddToVaultAsync(_docs.ActiveDoc);
            }
            finally
            {
                RefreshActive(); // the document may now have a path in the vault
            }
        });

        public void OnCheckOut() => Run("Check out", () => _commands.CheckOutAsync(Targets()));

        public void OnCheckIn() => Run("Check in", () => _commands.CheckInAsync(Targets()));

        public void OnUndoCheckOut() => Run("Undo check-out", () => _commands.UndoCheckOutAsync(Targets()));

        public void OnGetLatest() => Run("Get latest", () => _commands.GetLatestAsync(Targets(), withReferences: true));

        public void OnHistory() => Run("History", async () => { var t = Targets(); if (t.Count > 0) await _commands.HistoryAsync(t[0]); });

        public void OnWhereUsed() => Run("Where used", async () => { var t = Targets(); if (t.Count > 0) await _commands.WhereUsedAsync(t[0]); });

        public void OnChangeState() => Run("Change state", async () => { var t = Targets(); if (t.Count > 0) await _commands.ChangeStateAsync(t[0]); });

        public void OnImport() => Run("Import", async () =>
        {
            using (var wizard = new ImportWizard(_docs, _agent))
                wizard.ShowDialog(UiThread.Owner);
            await _pane.RefreshAllAsync(sync: false);
        });

        public void OnRefresh() => Run("Refresh", () => _pane.RefreshAllAsync(sync: true));

        public void OnRequestReview() => Run("Request review", async () =>
        {
            var t = Targets();
            if (t.Count > 0) await _agent.ShowAgentWindowAsync("requestReview", t[0]);
        });

        public void OnReviews() => Run("Reviews", () => _agent.ShowAgentWindowAsync("reviews"));

        public void OnSubsystems() => Run("Subsystems", () => _agent.ShowAgentWindowAsync("subsystems"));

        public void OnInvite() => Run("Invite people", () => _commands.InvitePeopleAsync());

        public void OnSettings() => Run("Vaults", async () =>
        {
            await _commands.SetupVaultAsync();
            await _pane.RefreshAllAsync(sync: false);
        });

        public void OnCheckOutSelected() => OnCheckOut();

        public void OnCheckInSelected() => OnCheckIn();

        public void OnGetLatestSelected() => OnGetLatest();

        public void OnHistorySelected() => OnHistory();

        /// <summary>1 = enabled, 0 = disabled (SOLIDWORKS enable-method contract).</summary>
        public int EnableWithDocument() => _sw?.ActiveDoc != null ? 1 : 0;

        public int EnableAlways() => 1;
    }
}
