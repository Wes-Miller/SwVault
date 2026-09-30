using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwApiCheck
{
    /// <summary>
    /// Verifies the SOLIDWORKS API behaviors SwVault depends on, using throwaway models:
    ///   A. GetDocumentDependencies2 lists references of closed files
    ///   B. ReplaceReferencedDocument re-points a closed assembly (bulk import)
    ///   C. a read-only file opens read-only; whether SOLIDWORKS locks read-only files
    ///   D. SetReadOnlyState(false) after check-out lets the open document be saved
    ///   E. ForceReleaseLocks + replace file + ReloadOrReplace picks up the new content (get latest)
    ///   F. drawings: close, replace, reopen
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Results = new List<string>();
        private static int _failures;

        [STAThread]
        private static int Main(string[] args)
        {
            if (Process.GetProcessesByName("SLDWORKS").Length > 0)
            {
                Console.WriteLine("SOLIDWORKS is running. Close it first: this check starts its own SOLIDWORKS and must not touch your open documents.");
                return 2;
            }

            var addInOnly = args.Contains("--addin");
            var folderArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            var root = Path.Combine(folderArg ?? @"C:\SwVaultDev\swapicheck", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            Console.WriteLine("Working folder: " + root);
            Console.WriteLine("Starting SOLIDWORKS (sign in if it asks)...");

            ISldWorks sw = null;
            try
            {
                sw = addInOnly ? LaunchLikeAUser() : (ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application"));
                sw.Visible = true;
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (!sw.StartupProcessCompleted && DateTime.UtcNow < deadline) Thread.Sleep(1000);
                Console.WriteLine("SOLIDWORKS " + sw.RevisionNumber() + " ready.");
                if (addInOnly) CheckAddIn(sw);
                else Run(sw, root);
            }
            catch (Exception ex)
            {
                Record(false, "Unexpected error", ex.ToString());
            }
            finally
            {
                if (sw != null)
                {
                    try
                    {
                        sw.CloseAllDocuments(true);
                        sw.ExitApp();
                    }
                    catch (COMException)
                    {
                    }
                    Marshal.ReleaseComObject(sw);
                }
            }

            File.WriteAllLines(Path.Combine(root, "results.txt"), Results);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void Record(bool ok, string check, string detail)
        {
            if (!ok) _failures++;
            var line = (ok ? "PASS  " : "FAIL  ") + check + (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail);
            Results.Add(line);
            Console.WriteLine(line);
        }

        private static void Run(ISldWorks sw, string root)
        {
            var part = Path.Combine(root, "Bracket.SLDPRT");
            var partV2 = Path.Combine(root, "Bracket_v2.SLDPRT");
            var asm = Path.Combine(root, "Corner.SLDASM");
            var drw = Path.Combine(root, "Bracket.SLDDRW");
            CreateModels(sw, part, partV2, asm, drw);

            // A. references of closed files
            var asmRefs = Dependencies(sw, asm, search: true);
            Record(asmRefs.Contains(part, StringComparer.OrdinalIgnoreCase), "A. GetDocumentDependencies2 on closed assembly", string.Join("; ", asmRefs));
            var drwRefs = Dependencies(sw, drw, search: true);
            Record(drwRefs.Contains(part, StringComparer.OrdinalIgnoreCase), "A. GetDocumentDependencies2 on closed drawing", string.Join("; ", drwRefs));

            // B. re-point a closed assembly
            var moved = Path.Combine(root, "moved");
            Directory.CreateDirectory(moved);
            var movedAsm = Path.Combine(moved, "Corner.SLDASM");
            var movedPart = Path.Combine(moved, "Bracket.SLDPRT");
            File.Copy(asm, movedAsm);
            File.Copy(part, movedPart);
            var replaced = sw.ReplaceReferencedDocument(movedAsm, part, movedPart);
            var movedRefs = Dependencies(sw, movedAsm, search: false);
            Record(replaced && movedRefs.Contains(movedPart, StringComparer.OrdinalIgnoreCase), "B. ReplaceReferencedDocument on closed assembly", "returned " + replaced + "; now " + string.Join("; ", movedRefs));

            // C. read-only file opens read-only
            File.SetAttributes(part, FileAttributes.ReadOnly);
            var asmDoc = Open(sw, asm);
            var partDoc = FindLoaded(sw, part);
            Record(partDoc != null && partDoc.IsOpenedReadOnly(), "C. read-only part loads read-only", partDoc == null ? "part not loaded" : "IsOpenedReadOnly=" + partDoc.IsOpenedReadOnly());
            Record(true, "C. info: SOLIDWORKS holds a write-blocking lock on a read-only loaded file", CanOpenForWrite(part, clearReadOnly: true) ? "no (file writable while loaded)" : "yes (file locked while loaded)");

            // D. check-out: clear attribute + SetReadOnlyState(false), then save
            File.SetAttributes(part, FileAttributes.Normal);
            var setOk = partDoc != null && partDoc.SetReadOnlyState(false);
            var nowReadOnly = partDoc?.IsOpenedReadOnly() ?? true;
            var saved = false;
            if (partDoc != null && !nowReadOnly)
            {
                SetProperty(partDoc, "Spike", "edited");
                int e = 0, w = 0;
                saved = partDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w);
            }
            Record(setOk && !nowReadOnly && saved, "D. SetReadOnlyState(false) then Save3", "set=" + setOk + " readOnly=" + nowReadOnly + " saved=" + saved);
            Record(true, "D. info: SOLIDWORKS locks a writable loaded file", CanOpenForWrite(part, clearReadOnly: false) ? "no" : "yes");

            // E. get latest under an open document
            var released = partDoc != null ? partDoc.ForceReleaseLocks() : -1;
            var copied = TryCopy(partV2, part);
            var reload = partDoc != null ? partDoc.ReloadOrReplace(false, part, true) : -1;
            partDoc = FindLoaded(sw, part);
            var value = partDoc != null ? GetProperty(partDoc, "Spike") : null;
            Record(copied && value == "v2", "E. ForceReleaseLocks + replace + ReloadOrReplace",
                "release=" + released + " copied=" + copied + " reload=" + (swComponentReloadError_e)reload + " property=" + value);
            sw.CloseDoc(asmDoc.GetTitle());

            // F. drawings: close, replace, reopen
            var drwDoc = Open(sw, drw);
            sw.CloseDoc(drwDoc.GetTitle());
            var drwCopy = Path.Combine(root, "Bracket_copy.SLDDRW");
            File.Copy(drw, drwCopy);
            var replacedDrw = TryCopy(drwCopy, drw);
            var reopened = Open(sw, drw);
            Record(replacedDrw && reopened != null, "F. drawing close / replace / reopen", "replaced=" + replacedDrw + " reopened=" + (reopened != null));
            sw.CloseAllDocuments(true);
        }

        /// <summary>
        /// Starts SLDWORKS.exe the way a user does (automation-started instances skip start-up
        /// add-ins), then attaches through the running object table.
        /// </summary>
        private static ISldWorks LaunchLikeAUser()
        {
            var exe = Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS", "SLDWORKS.exe");
            Process.Start(exe);
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                }
                catch (COMException)
                {
                    Thread.Sleep(2000);
                }
            }
            throw new TimeoutException("SOLIDWORKS did not register itself for automation within 5 minutes.");
        }

        /// <summary>Checks the registered SwVault add-in loaded and reached its agent.</summary>
        private static void CheckAddIn(ISldWorks sw)
        {
            const string clsid = "{7F3C2A51-9B4E-4C2D-A6E1-5D8B0F2C9E47}";
            object addIn = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (addIn == null && DateTime.UtcNow < deadline)
            {
                addIn = sw.GetAddInObject(clsid);
                if (addIn == null) Thread.Sleep(1000);
            }
            Record(addIn != null, "Add-in loaded in SOLIDWORKS", addIn == null ? "not loaded (check Tools > Add-Ins and the add-in log)" : addIn.GetType().FullName);

            var log = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SwVault", "logs", "addin-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var connected = false;
            deadline = DateTime.UtcNow.AddSeconds(40);
            while (!connected && DateTime.UtcNow < deadline)
            {
                connected = File.Exists(log) && ReadShared(log).Contains("Connected to the SwVault agent");
                if (!connected) Thread.Sleep(1000);
            }
            var tail = File.Exists(log) ? string.Join(" | ", ReadShared(log).Split('\n').Where(l => l.Trim().Length > 0).Reverse().Take(4).Reverse().Select(l => l.Trim())) : "no log";
            Record(connected, "Add-in connected to the SwVault agent", tail);
        }

        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        private static void CreateModels(ISldWorks sw, string part, string partV2, string asm, string drw)
        {
            var p = (IModelDoc2)sw.NewDocument(sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart), 0, 0, 0);
            p.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0);
            p.SketchManager.InsertSketch(true);
            p.SketchManager.CreateCornerRectangle(0, 0, 0, 0.05, 0.03, 0);
            p.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, 0.01, 0, false, false, false, false, 0, 0,
                false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            p.ClearSelection2(true);
            SetProperty(p, "Spike", "v1");
            SaveAs(p, part);
            SetProperty(p, "Spike", "v2");
            int e = 0, w = 0;
            p.Extension.SaveAs3(partV2, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, null, ref e, ref w);
            SetProperty(p, "Spike", "v1");
            p.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w);

            var a = (IModelDoc2)sw.NewDocument(sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly), 0, 0, 0);
            var component = ((IAssemblyDoc)a).AddComponent5(part, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", 0, 0, 0);
            Console.WriteLine("  component added: " + (component != null));
            SaveAs(a, asm);
            sw.CloseDoc(a.GetTitle());

            var d = (IModelDoc2)sw.NewDocument(sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing), (int)swDwgPaperSizes_e.swDwgPaperA4size, 0.297, 0.21);
            var view = ((IDrawingDoc)d).CreateDrawViewFromModelView3(part, "*Front", 0.1, 0.1, 0);
            Console.WriteLine("  drawing view created: " + (view != null));
            SaveAs(d, drw);
            sw.CloseDoc(d.GetTitle());
            sw.CloseDoc(p.GetTitle());
            Console.WriteLine("  models created");
        }

        private static void SaveAs(IModelDoc2 doc, string path)
        {
            int e = 0, w = 0;
            if (!doc.Extension.SaveAs3(path, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w))
                throw new InvalidOperationException("SaveAs3 failed for " + path + " (errors " + e + ")");
        }

        private static IModelDoc2 Open(ISldWorks sw, string path)
        {
            int e = 0, w = 0;
            var type = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? swDocumentTypes_e.swDocASSEMBLY
                : path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? swDocumentTypes_e.swDocDRAWING
                : swDocumentTypes_e.swDocPART;
            return sw.OpenDoc6(path, (int)type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w);
        }

        private static IModelDoc2 FindLoaded(ISldWorks sw, string path) =>
            ((object[])sw.GetDocuments() ?? new object[0]).OfType<IModelDoc2>().FirstOrDefault(d => string.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase));

        private static List<string> Dependencies(ISldWorks sw, string path, bool search)
        {
            var raw = sw.GetDocumentDependencies2(path, false, search, false);
            var values = raw as string[] ?? (raw as object[])?.Cast<string>().ToArray() ?? new string[0];
            var result = new List<string>();
            for (var i = 1; i < values.Length; i += 2) result.Add(values[i]);
            return result;
        }

        private static void SetProperty(IModelDoc2 doc, string name, string value) =>
            doc.Extension.get_CustomPropertyManager("").Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

        private static string GetProperty(IModelDoc2 doc, string name)
        {
            doc.Extension.get_CustomPropertyManager("").Get6(name, false, out var value, out var resolved, out _, out _);
            return string.IsNullOrEmpty(resolved) ? value : resolved;
        }

        private static bool CanOpenForWrite(string path, bool clearReadOnly)
        {
            var attributes = File.GetAttributes(path);
            try
            {
                if (clearReadOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true;
            }
            catch (IOException)
            {
                return false;
            }
            finally
            {
                File.SetAttributes(path, attributes);
            }
        }

        private static bool TryCopy(string from, string to)
        {
            try
            {
                if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
                File.Copy(from, to, overwrite: true);
                return true;
            }
            catch (IOException ex)
            {
                Console.WriteLine("  copy failed: " + ex.Message);
                return false;
            }
        }
    }
}
