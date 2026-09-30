using System.Globalization;

namespace SwVault.Agent;

/// <summary>Daily log files in %LOCALAPPDATA%\SwVault\logs; the first place to look when something goes wrong.</summary>
internal sealed class FileLog
{
    private readonly string _dir;
    private readonly object _sync = new();

    public FileLog(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "agent-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }

    public string Folder => _dir;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level,-5} {message}{Environment.NewLine}";
        var path = Path.Combine(_dir, $"agent-{DateTime.Now:yyyyMMdd}.log");
        lock (_sync)
        {
            try { File.AppendAllText(path, line); } catch (IOException) { }
        }
    }
}
