using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SwVault.Core.Git;

/// <summary>
/// A long-running <c>git cat-file --batch</c> process: reading thousands of small pointer and
/// sidecar blobs through one pipe is far faster than one process per blob.
/// </summary>
internal sealed class CatFileBatch : IDisposable
{
    private readonly string _gitDir;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public CatFileBatch(string gitDir, IReadOnlyDictionary<string, string?> environment)
    {
        _gitDir = gitDir;
        _environment = environment;
    }

    /// <summary>Returns (type, content) or null when the object does not exist.</summary>
    public async Task<(string Type, byte[] Content)?> ReadAsync(string objectSpec, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await ReadCoreAsync(objectSpec, ct).ConfigureAwait(false);
                }
                catch (IOException) when (attempt == 0)
                {
                    // The batch process died (e.g. after a repack); restart it once.
                    KillProcess();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Type, byte[] Content)?> ReadCoreAsync(string objectSpec, CancellationToken ct)
    {
        var process = EnsureProcess();
        var stdin = process.StandardInput.BaseStream;
        var request = Encoding.UTF8.GetBytes(objectSpec + "\n");
        await stdin.WriteAsync(request, ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);

        var stdout = process.StandardOutput.BaseStream;
        var header = await ReadLineAsync(stdout, ct).ConfigureAwait(false)
            ?? throw new IOException("git cat-file ended unexpectedly.");
        if (header.EndsWith(" missing", StringComparison.Ordinal) || header.EndsWith(" ambiguous", StringComparison.Ordinal))
            return null;

        // "<sha> <type> <size>"
        var parts = header.Split(' ');
        if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new IOException($"Unexpected git cat-file header: {header}");

        var content = new byte[size];
        await stdout.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        var terminator = new byte[1];
        await stdout.ReadExactlyAsync(terminator, ct).ConfigureAwait(false);
        return (parts[1], content);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.Add(one[0]);
        }
    }

    private Process EnsureProcess()
    {
        if (_process is { HasExited: false }) return _process;
        KillProcess();
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("--git-dir=" + _gitDir);
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch");
        foreach (var (key, value) in _environment)
        {
            if (value == null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
        _process = Process.Start(psi) ?? throw new IOException("Could not start git cat-file.");
        return _process;
    }

    private void KillProcess()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000)) _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        KillProcess();
        _gate.Dispose();
    }
}
