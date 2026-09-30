using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwVault.AddIn.Infrastructure;

namespace SwVault.AddIn.Sw
{
    /// <summary>A document SwVault took off disk so its file could be replaced, and how to bring it back.</summary>
    internal sealed class ReleasedDoc
    {
        public string Path { get; set; }
        public int Type { get; set; }

        /// <summary>Drawings can't release their locks in place, so they are closed and reopened.</summary>
        public bool Closed { get; set; }
    }

    /// <summary>SOLIDWORKS document helpers. Every method must run on SOLIDWORKS' main thread.</summary>
    internal sealed class SwDocs
    {
        private readonly ISldWorks _sw;

        public SwDocs(ISldWorks sw)
        {
            _sw = sw;
        }

        public ISldWorks App => _sw;

        public IModelDoc2 ActiveDoc => _sw.ActiveDoc as IModelDoc2;

        public static string PathOf(IModelDoc2 doc)
        {
            try
            {
                return doc?.GetPathName() ?? "";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return "";
            }
        }

        public static int TypeFromExtension(string path)
        {
            switch (System.IO.Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".sldprt": return (int)swDocumentTypes_e.swDocPART;
                case ".sldasm": return (int)swDocumentTypes_e.swDocASSEMBLY;
                case ".slddrw": return (int)swDocumentTypes_e.swDocDRAWING;
                default: return (int)swDocumentTypes_e.swDocNONE;
            }
        }

        public static bool IsSolidWorksFile(string path) => TypeFromExtension(path) != (int)swDocumentTypes_e.swDocNONE;

        public static bool HasReferences(string path)
        {
            var type = TypeFromExtension(path);
            return type == (int)swDocumentTypes_e.swDocASSEMBLY || type == (int)swDocumentTypes_e.swDocDRAWING;
        }

        /// <summary>All documents loaded in this session, including parts loaded only as assembly components.</summary>
        public IEnumerable<IModelDoc2> LoadedDocuments()
        {
            var docs = _sw.GetDocuments() as object[];
            if (docs == null) yield break;
            foreach (var d in docs)
                if (d is IModelDoc2 model) yield return model;
        }

        public IModelDoc2 FindOpen(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return LoadedDocuments().FirstOrDefault(d => string.Equals(PathOf(d), path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>SOLIDWORKS release, e.g. "2025 SP3" (from revision "33.3.0").</summary>
        public string Version()
        {
            var parts = (_sw.RevisionNumber() ?? "").Split('.');
            int major, minor;
            if (parts.Length < 2 || !int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor)) return null;
            return (1992 + major).ToString(CultureInfo.InvariantCulture) + " SP" + minor.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------ references and properties

        /// <summary>Referenced files as stored on disk (works for closed documents).</summary>
        public string[] References(string path, bool traverse)
        {
            if (!HasReferences(path) && TypeFromExtension(path) != (int)swDocumentTypes_e.swDocPART) return new string[0];
            object raw;
            try
            {
                raw = _sw.GetDocumentDependencies2(path, traverse, true, false);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Warn("GetDocumentDependencies2 failed for " + path + ": " + ex.Message);
                return new string[0];
            }
            // Pairs of [file name, full path].
            var values = raw as string[] ?? (raw as object[])?.Cast<string>().ToArray() ?? new string[0];
            var result = new List<string>();
            for (var i = 1; i < values.Length; i += 2)
            {
                var full = values[i];
                if (!string.IsNullOrEmpty(full) && !string.Equals(full, path, StringComparison.OrdinalIgnoreCase)
                    && !result.Contains(full, StringComparer.OrdinalIgnoreCase))
                    result.Add(full);
            }
            return result.ToArray();
        }

        public Dictionary<string, string> Properties(IModelDoc2 doc)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc == null) return props;
            try
            {
                var manager = doc.Extension.get_CustomPropertyManager("");
                var names = manager.GetNames() as object[];
                if (names == null) return props;
                foreach (var nameObj in names)
                {
                    var name = nameObj as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    manager.Get6(name, false, out var value, out var resolved, out _, out _);
                    props[name] = string.IsNullOrEmpty(resolved) ? value ?? "" : resolved;
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Warn("Reading custom properties failed: " + ex.Message);
            }
            return props;
        }

        public string[] Configurations(IModelDoc2 doc)
        {
            try
            {
                return (doc?.GetConfigurationNames() as object[])?.Cast<string>().ToArray() ?? new string[0];
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return new string[0];
            }
        }

        public void SetProperty(IModelDoc2 doc, string name, string value)
        {
            var manager = doc.Extension.get_CustomPropertyManager("");
            manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
        }

        // ------------------------------------------------------------ open, save, export

        public IModelDoc2 Open(string path, bool silent)
        {
            int errors = 0, warnings = 0;
            var options = silent ? (int)swOpenDocOptions_e.swOpenDocOptions_Silent : 0;
            var doc = _sw.OpenDoc6(path, TypeFromExtension(path), options, "", ref errors, ref warnings);
            if (doc == null) Log.Warn("OpenDoc6 failed for " + path + " (errors " + errors + ", warnings " + warnings + ")");
            return doc;
        }

        public void Close(IModelDoc2 doc)
        {
            if (doc != null) _sw.CloseDoc(doc.GetTitle());
        }

        public bool Save(IModelDoc2 doc)
        {
            int errors = 0, warnings = 0;
            var ok = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
            if (!ok) Log.Warn("Save3 failed for " + PathOf(doc) + " (errors " + errors + ")");
            return ok;
        }

        public bool Export(IModelDoc2 doc, string target)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            int errors = 0, warnings = 0;
            var ok = doc.Extension.SaveAs3(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
            if (!ok) Log.Warn("Export to " + target + " failed (errors " + errors + ")");
            return ok && File.Exists(target);
        }

        /// <summary>Saves open documents with unsaved changes among <paramref name="paths"/>.</summary>
        public List<string> SaveDirty(IEnumerable<string> paths)
        {
            var failed = new List<string>();
            foreach (var path in paths)
            {
                var doc = FindOpen(path);
                if (doc == null || !doc.GetSaveFlag()) continue;
                if (!Save(doc)) failed.Add(path);
            }
            return failed;
        }

        public List<string> DirtyAmong(IEnumerable<string> paths) =>
            paths.Where(p => FindOpen(p)?.GetSaveFlag() == true).ToList();

        // ------------------------------------------------------------ replacing files under open documents

        /// <summary>
        /// Lets SwVault overwrite files that are open: parts/assemblies release their file locks,
        /// drawings are closed. Documents with unsaved changes are left alone and reported.
        /// </summary>
        public List<ReleasedDoc> Release(IEnumerable<string> paths, List<string> blocked)
        {
            var released = new List<ReleasedDoc>();
            foreach (var path in paths)
            {
                var doc = FindOpen(path);
                if (doc == null) continue;
                if (doc.GetSaveFlag())
                {
                    blocked.Add(path);
                    continue;
                }
                var type = doc.GetType();
                try
                {
                    if (type == (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        Close(doc);
                        released.Add(new ReleasedDoc { Path = path, Type = type, Closed = true });
                    }
                    else
                    {
                        doc.ForceReleaseLocks();
                        released.Add(new ReleasedDoc { Path = path, Type = type });
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Log.Warn("Could not release " + path + ": " + ex.Message);
                    blocked.Add(path);
                }
            }
            return released;
        }

        /// <summary>Reattaches released documents to the (new) files on disk.</summary>
        public void Restore(IEnumerable<ReleasedDoc> released)
        {
            foreach (var r in released)
            {
                try
                {
                    if (r.Closed)
                    {
                        if (File.Exists(r.Path)) Open(r.Path, silent: true);
                        continue;
                    }
                    var doc = FindOpen(r.Path);
                    if (doc == null) continue;
                    var readOnly = File.Exists(r.Path) && File.GetAttributes(r.Path).HasFlag(FileAttributes.ReadOnly);
                    var result = doc.ReloadOrReplace(readOnly, r.Path, true);
                    if (result != (int)swComponentReloadError_e.swReloadOkay && result != (int)swComponentReloadError_e.swDocumentNotChanged)
                        Log.Warn("ReloadOrReplace " + r.Path + " returned " + (swComponentReloadError_e)result);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Log.Warn("Could not reload " + r.Path + ": " + ex.Message);
                }
            }
        }

        /// <summary>Makes SOLIDWORKS' read-only flag match the file attribute (after check-out / check-in).</summary>
        public void SyncReadOnlyState(IEnumerable<string> paths)
        {
            foreach (var path in paths ?? new string[0])
            {
                var doc = FindOpen(path);
                if (doc == null || !File.Exists(path)) continue;
                var readOnly = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
                try
                {
                    if (doc.IsOpenedReadOnly() != readOnly) doc.SetReadOnlyState(readOnly);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Log.Warn("SetReadOnlyState failed for " + path + ": " + ex.Message);
                }
            }
        }

        /// <summary>Paths of components selected in the FeatureManager tree of the active assembly.</summary>
        public List<string> SelectedComponentPaths()
        {
            var result = new List<string>();
            var doc = ActiveDoc;
            if (doc == null) return result;
            var selection = doc.SelectionManager as ISelectionMgr;
            if (selection == null) return result;
            var count = selection.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count; i++)
            {
                if (selection.GetSelectedObjectsComponent4(i, -1) is IComponent2 component)
                {
                    var path = component.GetPathName();
                    if (!string.IsNullOrEmpty(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                }
            }
            return result;
        }
    }
}
