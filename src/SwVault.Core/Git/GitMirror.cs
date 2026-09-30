using System.Globalization;
using System.Text;
using SwVault.Protocol;

namespace SwVault.Core.Git;

public sealed record TreeEntry(string Mode, string Type, string Sha, string Path);

public sealed record CommitInfo(string Sha, string AuthorName, string AuthorEmail, DateTimeOffset Date, string Subject);

/// <summary>A path to add or update (BlobSha set) or delete (BlobSha null) in a commit.</summary>
public sealed record TreeChange(string Path, string? BlobSha);

public sealed record GitIdentity(string Name, string Email);

public enum PushOutcome
{
    Pushed,
    Rejected,
}

public sealed class GitException : VaultException
{
    public GitException(string message) : base(ErrorCodes.Internal, message) { }
}

/// <summary>
/// A bare, pointer-only mirror of the vault repository. It never contains CAD bytes (LFS
/// content lives in the workspace), so it stays small and fast to fetch.
/// </summary>
public sealed class GitMirror : IDisposable
{
    public const string Branch = "main";
    public const string RemoteRef = "refs/remotes/origin/main";
    private const string EmptySha = "0000000000000000000000000000000000000000";

    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> _authEnvironment;
    private readonly CatFileBatch _catFile;

    public string GitDir { get; }
    public string RemoteUrl { get; }

    private GitMirror(string gitDir, string remoteUrl, Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> authEnvironment)
    {
        GitDir = gitDir;
        RemoteUrl = remoteUrl;
        _authEnvironment = authEnvironment;
        _catFile = new CatFileBatch(gitDir, BaseEnvironment());
    }

    /// <summary>Opens the mirror, creating and configuring an empty bare repo on first use.</summary>
    public static async Task<GitMirror> OpenOrCreateAsync(
        string gitDir,
        string remoteUrl,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> authEnvironment,
        CancellationToken ct)
    {
        var mirror = new GitMirror(gitDir, remoteUrl, authEnvironment);
        if (!File.Exists(Path.Combine(gitDir, "HEAD")))
        {
            Directory.CreateDirectory(gitDir);
            await mirror.RunLocalAsync(new[] { "init", "--bare", "--initial-branch=" + Branch, gitDir }, gitDirArg: false, ct: ct).ConfigureAwait(false);
        }

        var hooks = Path.Combine(gitDir, "no-hooks");
        Directory.CreateDirectory(hooks);
        // Idempotent config: the mirror must never run hooks, convert line endings, or smudge LFS content.
        foreach (var (key, value) in new[]
        {
            ("remote.origin.url", remoteUrl),
            ("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"),
            ("core.hooksPath", hooks),
            ("core.autocrlf", "false"),
            ("fetch.prune", "true"),
            ("gc.autoDetach", "false"),
        })
        {
            await mirror.RunLocalAsync(new[] { "config", key, value }, ct: ct).ConfigureAwait(false);
        }
        return mirror;
    }

    /// <summary>Environment for every git call: never prompt, never smudge, English messages.</summary>
    private static Dictionary<string, string?> BaseEnvironment() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_LFS_SKIP_SMUDGE"] = "1",
        ["LC_ALL"] = "C",
        ["LANG"] = "C",
        ["GIT_INDEX_FILE"] = null,
        ["GIT_DIR"] = null,
    };

    public async Task<string?> LsRemoteHeadAsync(CancellationToken ct)
    {
        var result = await RunRemoteAsync(new[] { "ls-remote", "origin", "refs/heads/" + Branch }, ct).ConfigureAwait(false);
        EnsureSuccess(result, "list the server branch");
        var line = result.StdOutText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line?.Split('\t')[0].Trim();
    }

    /// <summary>Fetches main. Returns false when the server has no main branch yet (brand-new vault).</summary>
    public async Task<bool> FetchAsync(CancellationToken ct)
    {
        var result = await RunRemoteAsync(new[] { "fetch", "--no-tags", "--quiet", "origin", $"+refs/heads/{Branch}:{RemoteRef}" }, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.StdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
            return false;
        EnsureSuccess(result, "fetch from the server");
        return true;
    }

    public async Task<string?> GetHeadAsync(CancellationToken ct)
    {
        var result = await RunLocalAsync(new[] { "rev-parse", "--verify", "--quiet", RemoteRef + "^{commit}" }, check: false, ct: ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StdOutText.Trim() : null;
    }

    public async Task<IReadOnlyList<TreeEntry>> LsTreeAsync(string commit, CancellationToken ct)
    {
        var result = await RunLocalAsync(new[] { "ls-tree", "-r", "-z", "--full-tree", commit }, ct: ct).ConfigureAwait(false);
        var entries = new List<TreeEntry>();
        foreach (var record in Encoding.UTF8.GetString(result.StdOut).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record.Substring(0, tab).Split(' ');
            if (meta.Length != 3) continue;
            entries.Add(new TreeEntry(meta[0], meta[1], meta[2], record.Substring(tab + 1)));
        }
        return entries;
    }

    /// <summary>Reads an object by sha or by "commit:path".</summary>
    public async Task<byte[]?> ReadBlobAsync(string objectSpec, CancellationToken ct)
    {
        var result = await _catFile.ReadAsync(objectSpec, ct).ConfigureAwait(false);
        return result?.Type == "blob" ? result.Value.Content : null;
    }

    /// <summary>Commits (newest first) on main that touched <paramref name="path"/>.</summary>
    public async Task<IReadOnlyList<CommitInfo>> LogPathAsync(string path, int max, CancellationToken ct)
    {
        var args = new List<string> { "log", "--format=%H%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e" };
        if (max > 0) args.Add("-n" + max.ToString(CultureInfo.InvariantCulture));
        args.AddRange(new[] { RemoteRef, "--", path });
        var result = await RunLocalAsync(args, check: false, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return Array.Empty<CommitInfo>();
        var commits = new List<CommitInfo>();
        foreach (var record in result.StdOutText.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 5) continue;
            commits.Add(new CommitInfo(f[0], f[1], f[2], DateTimeOffset.Parse(f[3], CultureInfo.InvariantCulture), f[4]));
        }
        return commits;
    }

    /// <summary>Writes blobs into the object store and returns their shas in order.</summary>
    public async Task<IReadOnlyList<string>> WriteBlobsAsync(IReadOnlyList<byte[]> contents, CancellationToken ct)
    {
        if (contents.Count == 0) return Array.Empty<string>();
        var temp = Path.Combine(GitDir, "swvault-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var paths = new List<string>(contents.Count);
            for (var i = 0; i < contents.Count; i++)
            {
                var p = Path.Combine(temp, i.ToString(CultureInfo.InvariantCulture));
                await File.WriteAllBytesAsync(p, contents[i], ct).ConfigureAwait(false);
                paths.Add(p);
            }
            var stdin = Encoding.UTF8.GetBytes(string.Join("\n", paths) + "\n");
            var result = await RunLocalAsync(new[] { "hash-object", "-w", "--no-filters", "--stdin-paths" }, stdin: stdin, ct: ct).ConfigureAwait(false);
            var shas = result.StdOutText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            if (shas.Count != contents.Count) throw new GitException("git hash-object returned an unexpected number of objects.");
            return shas;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Builds a commit on top of <paramref name="parent"/> without touching any working tree.</summary>
    public async Task<string> CreateCommitAsync(string? parent, IReadOnlyList<TreeChange> changes, string message, GitIdentity author, CancellationToken ct)
    {
        var indexFile = Path.Combine(GitDir, "swvault-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = indexFile };
        try
        {
            await RunLocalAsync(parent == null ? new[] { "read-tree", "--empty" } : new[] { "read-tree", parent }, extraEnv: env, ct: ct).ConfigureAwait(false);

            if (changes.Count > 0)
            {
                var info = new StringBuilder();
                foreach (var change in changes)
                {
                    info.Append(change.BlobSha == null ? $"0 {EmptySha}" : $"100644 {change.BlobSha}");
                    info.Append('\t').Append(change.Path).Append('\0');
                }
                await RunLocalAsync(new[] { "update-index", "-z", "--index-info" }, stdin: Encoding.UTF8.GetBytes(info.ToString()), extraEnv: env, ct: ct).ConfigureAwait(false);
            }

            var tree = (await RunLocalAsync(new[] { "write-tree" }, extraEnv: env, ct: ct).ConfigureAwait(false)).StdOutText.Trim();

            env["GIT_AUTHOR_NAME"] = author.Name;
            env["GIT_AUTHOR_EMAIL"] = author.Email;
            env["GIT_COMMITTER_NAME"] = author.Name;
            env["GIT_COMMITTER_EMAIL"] = author.Email;
            var args = new List<string> { "commit-tree", tree };
            if (parent != null) args.AddRange(new[] { "-p", parent });
            args.AddRange(new[] { "-F", "-" });
            var commit = await RunLocalAsync(args, stdin: Encoding.UTF8.GetBytes(message.Replace("\r\n", "\n") + "\n"), extraEnv: env, ct: ct).ConfigureAwait(false);
            return commit.StdOutText.Trim();
        }
        finally
        {
            try { File.Delete(indexFile); } catch (IOException) { }
        }
    }

    /// <summary>Pushes a commit to main. A non-fast-forward rejection means someone else pushed first.</summary>
    public async Task<PushOutcome> PushAsync(string commit, CancellationToken ct)
    {
        var result = await RunRemoteAsync(new[] { "push", "--porcelain", "origin", $"{commit}:refs/heads/{Branch}" }, ct).ConfigureAwait(false);
        var output = result.StdOutText + "\n" + result.StdErr;
        if (result.ExitCode == 0) return PushOutcome.Pushed;
        if (output.Contains("[rejected]", StringComparison.Ordinal)
            || output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || output.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
            return PushOutcome.Rejected;
        if (output.Contains("[remote rejected]", StringComparison.Ordinal))
            throw new VaultException(ErrorCodes.Forbidden, "The server rejected the check-in: " + FirstMeaningfulLine(output));
        throw ClassifyRemoteFailure(result, "push to the server");
    }

    public Task UpdateRemoteRefAsync(string commit, CancellationToken ct) =>
        RunLocalAsync(new[] { "update-ref", RemoteRef, commit }, ct: ct);

    private async Task<ProcessResult> RunRemoteAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var auth = await _authEnvironment(ct).ConfigureAwait(false);
        var env = BaseEnvironment();
        foreach (var (k, v) in auth) env[k] = v;
        var all = new List<string> { "--git-dir=" + GitDir };
        all.AddRange(args);
        return await ProcessRunner.RunAsync("git", all, environment: env, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunLocalAsync(
        IEnumerable<string> args,
        byte[]? stdin = null,
        IReadOnlyDictionary<string, string?>? extraEnv = null,
        bool check = true,
        bool gitDirArg = true,
        CancellationToken ct = default)
    {
        var env = BaseEnvironment();
        if (extraEnv != null) foreach (var (k, v) in extraEnv) env[k] = v;
        var all = new List<string>();
        if (gitDirArg) all.Add("--git-dir=" + GitDir);
        all.AddRange(args);
        var result = await ProcessRunner.RunAsync("git", all, environment: env, stdin: stdin, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        if (check && result.ExitCode != 0)
            throw new GitException($"git {string.Join(' ', args.Take(2))} failed: {FirstMeaningfulLine(result.StdErr)}");
        return result;
    }

    private static void EnsureSuccess(ProcessResult result, string action)
    {
        if (result.ExitCode != 0) throw ClassifyRemoteFailure(result, action);
    }

    private static VaultException ClassifyRemoteFailure(ProcessResult result, string action)
    {
        var err = result.StdErr + "\n" + result.StdOutText;
        if (err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || err.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || err.Contains("returned error: 401", StringComparison.OrdinalIgnoreCase)
            || err.Contains("returned error: 403", StringComparison.OrdinalIgnoreCase)
            || err.Contains("remote: Unauthorized", StringComparison.OrdinalIgnoreCase))
            return VaultException.Unauthorized($"Could not {action}: not signed in or no access. ({FirstMeaningfulLine(err)})");
        if (err.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase)
            || err.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            || err.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || err.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || err.Contains("unable to access", StringComparison.OrdinalIgnoreCase))
            return VaultException.Offline($"Could not {action}: the vault server is unreachable. ({FirstMeaningfulLine(err)})");
        return new GitException($"Could not {action}: {FirstMeaningfulLine(err)}");
    }

    private static string FirstMeaningfulLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("hint:", StringComparison.Ordinal)) ?? "(no details)";

    public void Dispose() => _catFile.Dispose();
}
