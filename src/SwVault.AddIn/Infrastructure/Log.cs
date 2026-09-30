using System;
using System.Globalization;
using System.IO;

namespace SwVault.AddIn.Infrastructure
{
    /// <summary>Add-in log at %LOCALAPPDATA%\SwVault\logs\addin-yyyyMMdd.log.</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwVault", "logs");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : message + ": " + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Folder);
                    var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level.PadRight(5) + " " + message + Environment.NewLine;
                    File.AppendAllText(Path.Combine(Folder, "addin-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"), line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
