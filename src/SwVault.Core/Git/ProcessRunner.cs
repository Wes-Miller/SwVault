using System.Diagnostics;
using System.Text;

namespace SwVault.Core.Git;

public sealed record ProcessResult(int ExitCode, byte[] StdOut, string StdErr)
{
    public string StdOutText => Encoding.UTF8.GetString(StdOut);
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        byte[]? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                if (value == null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new VaultException(Protocol.ErrorCodes.Internal, $"Could not start '{fileName}'. Is it installed and on PATH? {ex.Message}", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) timeoutCts.CancelAfter(timeout.Value);
        var token = timeoutCts.Token;

        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            if (stdin != null) await process.StandardInput.BaseStream.WriteAsync(stdin, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"'{fileName} {string.Join(' ', psi.ArgumentList)}' timed out after {timeout}.");
        }
        catch (IOException) when (process.HasExited)
        {
            // The child closed stdin early (e.g. it failed before reading); report its exit instead.
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }
}
