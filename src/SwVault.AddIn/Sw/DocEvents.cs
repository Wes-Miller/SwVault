using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwVault.AddIn.Infrastructure;

namespace SwVault.AddIn.Sw
{
    /// <summary>
    /// Subscribes to modify/save/close events of every loaded document and forwards them as
    /// path-based events. Handlers never block SOLIDWORKS: work is posted to the UI queue.
    /// </summary>
    internal sealed class DocEvents : IDisposable
    {
        private readonly Dictionary<object, Action> _detach = new Dictionary<object, Action>(ReferenceComparer.Instance);

        /// <summary>First modification of a document (it just became dirty).</summary>
        public event Action<IModelDoc2> Modified;

        public event Action<IModelDoc2, string> Saved;

        public event Action<string> Closed;

        public void Attach(IModelDoc2 doc)
        {
            if (doc == null || _detach.ContainsKey(doc)) return;
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                {
                    var part = (PartDoc)doc;
                    DPartDocEvents_ModifyNotifyEventHandler modify = () => Raise(() => Modified?.Invoke(doc));
                    DPartDocEvents_FileSavePostNotifyEventHandler save = (type, name) => Raise(() => Saved?.Invoke(doc, name));
                    DPartDocEvents_DestroyNotify2EventHandler destroy = t => OnDestroy(doc, t);
                    part.ModifyNotify += modify;
                    part.FileSavePostNotify += save;
                    part.DestroyNotify2 += destroy;
                    _detach[doc] = () =>
                    {
                        part.ModifyNotify -= modify;
                        part.FileSavePostNotify -= save;
                        part.DestroyNotify2 -= destroy;
                    };
                    break;
                }
                case swDocumentTypes_e.swDocASSEMBLY:
                {
                    var assembly = (AssemblyDoc)doc;
                    DAssemblyDocEvents_ModifyNotifyEventHandler modify = () => Raise(() => Modified?.Invoke(doc));
                    DAssemblyDocEvents_FileSavePostNotifyEventHandler save = (type, name) => Raise(() => Saved?.Invoke(doc, name));
                    DAssemblyDocEvents_DestroyNotify2EventHandler destroy = t => OnDestroy(doc, t);
                    assembly.ModifyNotify += modify;
                    assembly.FileSavePostNotify += save;
                    assembly.DestroyNotify2 += destroy;
                    _detach[doc] = () =>
                    {
                        assembly.ModifyNotify -= modify;
                        assembly.FileSavePostNotify -= save;
                        assembly.DestroyNotify2 -= destroy;
                    };
                    break;
                }
                case swDocumentTypes_e.swDocDRAWING:
                {
                    var drawing = (DrawingDoc)doc;
                    DDrawingDocEvents_ModifyNotifyEventHandler modify = () => Raise(() => Modified?.Invoke(doc));
                    DDrawingDocEvents_FileSavePostNotifyEventHandler save = (type, name) => Raise(() => Saved?.Invoke(doc, name));
                    DDrawingDocEvents_DestroyNotify2EventHandler destroy = t => OnDestroy(doc, t);
                    drawing.ModifyNotify += modify;
                    drawing.FileSavePostNotify += save;
                    drawing.DestroyNotify2 += destroy;
                    _detach[doc] = () =>
                    {
                        drawing.ModifyNotify -= modify;
                        drawing.FileSavePostNotify -= save;
                        drawing.DestroyNotify2 -= destroy;
                    };
                    break;
                }
            }
        }

        private static int Raise(Action action)
        {
            UiThread.Post(action);
            return 0;
        }

        private int OnDestroy(IModelDoc2 doc, int destroyType)
        {
            if (destroyType != (int)swDestroyNotifyType_e.swDestroyNotifyDestroy) return 0;
            var path = SwDocs.PathOf(doc);
            if (_detach.TryGetValue(doc, out var detach))
            {
                _detach.Remove(doc);
                // Unsubscribe after SOLIDWORKS finishes raising this event.
                UiThread.Post(detach);
            }
            UiThread.Post(() => Closed?.Invoke(path));
            return 0;
        }

        public void Dispose()
        {
            foreach (var detach in _detach.Values)
            {
                try
                {
                    detach();
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }
            }
            _detach.Clear();
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
