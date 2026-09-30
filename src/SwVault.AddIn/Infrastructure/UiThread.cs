using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.Protocol;

namespace SwVault.AddIn.Infrastructure
{
    /// <summary>
    /// SOLIDWORKS API calls must happen on SOLIDWORKS' main thread. Everything async in the add-in
    /// resumes there through a WinForms synchronization context installed at start-up.
    /// </summary>
    internal static class UiThread
    {
        private static Control _marshal;

        public static IWin32Window Owner { get; set; }

        public static void Initialize()
        {
            _marshal = new Control();
            _marshal.CreateControl();
            EnsureContext();
        }

        /// <summary>Called at every entry point from SOLIDWORKS (commands, events).</summary>
        public static void EnsureContext()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        /// <summary>Runs on the UI thread (from agent notifications, which arrive on background threads).</summary>
        public static void Post(Action action)
        {
            if (_marshal == null || _marshal.IsDisposed) return;
            try
            {
                _marshal.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("UI callback failed", ex);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Runs a command body, turning every failure into a message instead of an exception that
        /// could take SOLIDWORKS down.
        /// </summary>
        public static async void Run(string what, Func<Task> body)
        {
            EnsureContext();
            try
            {
                await body();
            }
            catch (AgentException ex)
            {
                Log.Warn(what + ": " + ex.Message);
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(what + " failed", ex);
                ShowError("SwVault: " + what + " failed.\n\n" + ex.Message);
            }
        }

        public static void ShowError(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static void ShowInfo(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);

        public static bool Confirm(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        public static void Dispose()
        {
            _marshal?.Dispose();
            _marshal = null;
        }
    }

    /// <summary>Wraps a window handle (the SOLIDWORKS main frame) as a dialog owner.</summary>
    internal sealed class WindowHandle : IWin32Window
    {
        public WindowHandle(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
