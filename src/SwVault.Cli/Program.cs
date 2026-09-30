using System.CommandLine;
using System.Globalization;
using SwVault.Core;
using SwVault.Core.Auth;
using SwVault.Core.Client;
using SwVault.Core.Lfs;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Cli;

public static class Program
{
    private static readonly Option<string?> ProfileOption = new("--profile") { Description = "SwVault profile (simulate another user on this PC).", Recursive = true };
    private static readonly Option<string?> VaultOption = new("--vault") { Description = "Vault id (see 'swvault vault list'). Defaults to the vault containing the path.", Recursive = true };

    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("SwVault: check out, check in and share SOLIDWORKS files through a Git LFS server.");
        root.Options.Add(ProfileOption);
        root.Options.Add(VaultOption);

        root.Subcommands.Add(VaultCommands());
        root.Subcommands.Add(LoginCommand());
        root.Subcommands.Add(StatusCommand());
        root.Subcommands.Add(ListCommand());
        root.Subcommands.Add(GetCommand());
        root.Subcommands.Add(CheckOutCommand());
        root.Subcommands.Add(CheckInCommand());
        root.Subcommands.Add(UndoCommand());
        root.Subcommands.Add(HistoryCommand());
        root.Subcommands.Add(GetVersionCommand());
        root.Subcommands.Add(RollbackCommand());
        root.Subcommands.Add(TransitionCommand());
        root.Subcommands.Add(DeleteCommand());
        root.Subcommands.Add(LocksCommand());
        root.Subcommands.Add(UnlockCommand());
        root.Subcommands.Add(RefsCommand());
        root.Subcommands.Add(ImportCommand());
        root.Subcommands.Add(DoctorCommand());
        return await root.Parse(args).InvokeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private static Command Define(string name, string description, Func<ParseResult, VaultManager, CancellationToken, Task> body, params Symbol[] symbols)
    {
        var command = new Command(name, description);
        foreach (var symbol in symbols)
        {
            if (symbol is Option option) command.Options.Add(option);
            else if (symbol is Argument argument) command.Arguments.Add(argument);
        }
        command.SetAction(async (parse, ct) =>
        {
            using var manager = new VaultManager(new SwVaultProfile(parse.GetValue(ProfileOption)));
            try
            {
                await body(parse, manager, ct);
                return 0;
            }
            catch (VaultException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ex.Code == ErrorCodes.Conflict ? 2 : 1;
            }
        });
        return command;
    }

    private static Argument<string[]> PathsArgument(string description = "Files or folders (paths inside the vault folder).", bool required = true) =>
        new("paths") { Description = description, Arity = required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore };

    private static string[] FullPaths(string[]? paths) =>
        (paths ?? Array.Empty<string>()).Select(p => Path.GetFullPath(p)).ToArray();

    private static async Task<VaultSession> VaultFor(ParseResult parse, VaultManager manager, string[] paths, CancellationToken ct) =>
        await manager.ResolveAsync(parse.GetValue(VaultOption), paths.Length > 0 ? paths[0] : Environment.CurrentDirectory, ct);

    private static IProgress<TransferProgress> ConsoleProgress()
    {
        var last = DateTime.MinValue;
        return new Progress<TransferProgress>(p =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 500 && p.FilesDone < p.FilesTotal) return;
            last = DateTime.UtcNow;
            Console.Error.Write($"\r  {p.FilesDone}/{p.FilesTotal} files, {FormatSize(p.BytesDone)} of {FormatSize(p.BytesTotal)}   ");
            if (p.FilesDone >= p.FilesTotal) Console.Error.WriteLine();
        });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:N2} GB",
    };

    private static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var w in warnings) Console.WriteLine("  ! " + w);
    }

    private static async Task ApplyAndReport(VaultSession session, StagedOperation op, CancellationToken ct)
    {
        var result = await session.ApplyAsync(op, ct);
        foreach (var path in result.Applied) Console.WriteLine("  " + path);
        foreach (var path in result.Skipped) Console.WriteLine("  ! in use, not replaced: " + path);
        PrintWarnings(op.Warnings.Concat(result.Warnings));
    }

    private static string Describe(FileStatusDto s)
    {
        var state = s.LocalState switch
        {
            LocalState.NotLocal => "not local",
            LocalState.UpToDate => "up to date",
            LocalState.Outdated => $"outdated (v{s.LocalVersion} < v{s.ServerVersion})",
            LocalState.Modified => "modified",
            LocalState.Conflict => "CONFLICT",
            LocalState.LocalOnly => "new (not in vault)",
            LocalState.MissingLocally => "missing locally",
            LocalState.DeletedOnServer => "deleted on server",
            _ => s.LocalState.ToString(),
        };
        var lockText = s.LockState switch
        {
            LockState.MineHere => "checked out by you",
            LockState.MineElsewhere => "checked out by you on another PC",
            LockState.Other => "checked out by " + s.LockOwner,
            _ => "",
        };
        var workflow = s.State == null ? "" : s.State + (string.IsNullOrEmpty(s.Revision) ? "" : " rev " + s.Revision);
        return string.Join("  ", new[] { $"v{s.ServerVersion}".PadRight(5), state.PadRight(26), workflow.PadRight(16), lockText }).TrimEnd();
    }

    // ------------------------------------------------------------------ commands

    private static Command VaultCommands()
    {
        var vault = new Command("vault", "Set up vaults on this PC.");

        var url = new Argument<string>("url") { Description = "Repository URL, e.g. https://git.example.edu/fsae/vault.git" };
        var rootOption = new Option<string?>("--root") { Description = "Local vault folder. Defaults to the vault's shared folder (e.g. C:\\SWVault\\FSAE)." };
        var user = new Option<string?>("--user") { Description = "User name on the server." };
        var token = new Option<string?>("--token") { Description = "Access token (saved in Windows Credential Manager)." };
        vault.Subcommands.Add(Define("add", "Connect this PC to an existing vault.", async (parse, manager, ct) =>
        {
            var credential = parse.GetValue(user) is { } u && parse.GetValue(token) is { } t ? new Credential(u, t) : null;
            var session = await manager.AddAsync(parse.GetValue(url)!, parse.GetValue(rootOption), credential, ct);
            Console.WriteLine($"Connected '{session.Config.Name}' ({session.VaultId}) as {session.User?.Login}");
            Console.WriteLine($"Vault folder: {session.LocalRoot}");
            Console.WriteLine($"{session.Head.Files.Count} files on the server. Use 'swvault get {session.LocalRoot}' to download them.");
        }, url, rootOption, user, token));

        vault.Subcommands.Add(Define("list", "List vaults set up on this PC.", async (parse, manager, ct) =>
        {
            foreach (var r in manager.Registrations) Console.WriteLine($"{r.Id,-24} {r.Name,-16} {r.LocalRoot,-28} {r.RemoteUrl}");
            await Task.CompletedTask;
        }));

        var name = new Option<string>("--name") { Description = "Vault display name.", Required = true };
        var initRoot = new Option<string>("--root") { Description = "Shared local folder every PC will use, e.g. C:\\SWVault\\FSAE.", Required = true };
        var swVersion = new Option<string?>("--sw-version") { Description = "SOLIDWORKS major version everyone must use, e.g. 2025." };
        var create = new Option<bool>("--create") { Description = "Create the repository on the server first (Gitea or GitHub)." };
        vault.Subcommands.Add(Define("init", "Initialize a new, empty vault repository (you become its admin).", async (parse, manager, ct) =>
        {
            var remote = parse.GetValue(url)!;
            var credential = parse.GetValue(user) is { } u && parse.GetValue(token) is { } t ? new Credential(u, t) : null;
            if (parse.GetValue(create))
            {
                if (credential != null && Uri.TryCreate(remote, UriKind.Absolute, out var server))
                    new WindowsCredentialStore(manager.Profile.Name).Save(server, credential);
                using var http = new HttpClient();
                var auth = new AuthContext(new Uri(remote), manager.Profile.CredentialProvider);
                var host = SwVault.Core.Hosts.HostAdapters.Create(new Uri(remote), http, auth);
                var (_, owner, repo) = SwVault.Core.Hosts.HostAdapters.ParseRemote(new Uri(remote));
                await host.CreateRepositoryAsync(owner, repo, isPrivate: true, ct);
                Console.WriteLine($"Created repository {owner}/{repo}.");
            }
            var session = await manager.AddAsync(remote, parse.GetValue(initRoot), credential, ct);
            var config = VaultConfig.CreateDefault(parse.GetValue(name)!, parse.GetValue(initRoot)!, session.User!.Login, parse.GetValue(swVersion));
            await session.InitializeAsync(config, ct);
            manager.UpdateName(session.VaultId, config.Name);
            Console.WriteLine($"Initialized vault '{config.Name}'. {session.User.Login} is admin and approver; edit roles in .swvault/vault.json.");
        }, url, name, initRoot, swVersion, create, user, token));

        return vault;
    }

    private static Command LoginCommand()
    {
        var url = new Argument<string>("url") { Description = "Server or repository URL." };
        var user = new Option<string>("--user") { Description = "User name.", Required = true };
        var token = new Option<string?>("--token") { Description = "Access token (prompted if omitted)." };
        return Define("login", "Save an access token for a server in Windows Credential Manager.", async (parse, manager, ct) =>
        {
            var secret = parse.GetValue(token);
            if (string.IsNullOrEmpty(secret))
            {
                Console.Write("Access token: ");
                secret = ReadHidden();
            }
            var server = new Uri(parse.GetValue(url)!);
            new WindowsCredentialStore(manager.Profile.Name).Save(server, new Credential(parse.GetValue(user)!, secret));
            Console.WriteLine($"Saved credentials for {server.GetLeftPart(UriPartial.Authority)}.");
            await Task.CompletedTask;
        }, url, user, token);
    }

    private static string ReadHidden()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static Command StatusCommand()
    {
        var paths = PathsArgument("Files to show (default: your check-outs and local changes).", required: false);
        return Define("status", "Show local state, versions, workflow state and check-outs.", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            await session.TrySyncAsync(ct: ct);
            IEnumerable<FileStatusDto> statuses;
            if (targets.Length == 0)
            {
                var mine = await session.SearchAsync(null, StatusFilter.MyCheckouts, 1000, ct);
                var changed = await session.SearchAsync(null, StatusFilter.Modified, 1000, ct);
                var outdated = await session.SearchAsync(null, StatusFilter.Outdated, 1000, ct);
                statuses = mine.Concat(changed).Concat(outdated).DistinctBy(s => s.Path.ToLowerInvariant());
            }
            else
            {
                var list = new List<FileStatusDto>();
                foreach (var t in targets)
                {
                    if (Directory.Exists(t)) list.AddRange((await session.ListFolderAsync(SwVault.Core.Util.PathRules.ToVaultPath(session.LocalRoot, t) ?? "", ct)).Where(s => !s.IsFolder));
                    else if (await session.GetStatusAsync(t, ct) is { } s) list.Add(s);
                }
                statuses = list;
            }
            var any = false;
            foreach (var s in statuses)
            {
                any = true;
                Console.WriteLine($"{s.Path,-50} {Describe(s)}");
            }
            if (!any) Console.WriteLine("Nothing checked out, modified or outdated.");
            if (!session.Online) Console.WriteLine($"(offline: {session.LastError})");
        }, paths);
    }

    private static Command ListCommand()
    {
        var folder = new Argument<string?>("folder") { Description = "Folder (default: current).", Arity = ArgumentArity.ZeroOrOne };
        return Define("ls", "List a vault folder with each file's status.", async (parse, manager, ct) =>
        {
            var target = Path.GetFullPath(parse.GetValue(folder) ?? Environment.CurrentDirectory);
            var session = await VaultFor(parse, manager, new[] { target }, ct);
            await session.TrySyncAsync(ct: ct);
            var vaultFolder = SwVault.Core.Util.PathRules.ToVaultPath(session.LocalRoot, target) ?? "";
            foreach (var s in await session.ListFolderAsync(vaultFolder, ct))
                Console.WriteLine(s.IsFolder ? $"{SwVault.Core.Util.PathRules.GetFileName(s.Path) + "/",-40}" : $"{SwVault.Core.Util.PathRules.GetFileName(s.Path),-40} {Describe(s)}");
        }, folder);
    }

    private static Command GetCommand()
    {
        var paths = PathsArgument(required: false);
        var refs = new Option<bool>("--refs") { Description = "Also get everything the files reference (assemblies, drawings)." };
        return Define("get", "Get the latest version (files, folders, or the whole vault).", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var op = await session.PrepareGetLatestAsync(targets.Length == 0 ? new[] { session.LocalRoot } : targets, parse.GetValue(refs), ConsoleProgress(), ct);
            if (op.Changes.Count == 0) Console.WriteLine("Everything is up to date.");
            await ApplyAndReport(session, op, ct);
        }, paths, refs);
    }

    private static Command CheckOutCommand()
    {
        var paths = PathsArgument();
        var takeOver = new Option<bool>("--take-over") { Description = "Take over a check-out you hold on another PC." };
        var forTransition = new Option<string?>("--for") { Description = "Check out for a workflow transition (e.g. \"Approve\") on a read-only state." };
        return Define("checkout", "Lock files for editing (check out).", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var op = await session.CheckOutAsync(targets, parse.GetValue(takeOver), parse.GetValue(forTransition), ConsoleProgress(), ct);
            Console.WriteLine("Checked out:");
            await ApplyAndReport(session, op, ct);
        }, paths, takeOver, forTransition);
    }

    private static Command CheckInCommand()
    {
        var paths = PathsArgument();
        var message = new Option<string?>("-m", "--message") { Description = "Check-in comment." };
        var keep = new Option<bool>("--keep") { Description = "Keep the files checked out after checking in." };
        var transition = new Option<string?>("--transition") { Description = "Apply a workflow transition in the same check-in." };
        return Define("checkin", "Upload new versions and release the check-outs.", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var files = targets.SelectMany(t => Directory.Exists(t) ? Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories) : new[] { t }).ToList();
            var result = await session.CheckInAsync(files, null, new CheckInOptions
            {
                Comment = parse.GetValue(message),
                KeepCheckedOut = parse.GetValue(keep),
                TransitionName = parse.GetValue(transition),
            }, ConsoleProgress(), ct);
            foreach (var v in result.NewVersions) Console.WriteLine("  " + v);
            if (result.NewVersions.Count == 0) Console.WriteLine("  No content changes; check-outs released.");
            PrintWarnings(result.Warnings);
        }, paths, message, keep, transition);
    }

    private static Command UndoCommand()
    {
        var paths = PathsArgument();
        var discard = new Option<bool>("--discard") { Description = "Discard local changes." };
        return Define("undo", "Undo check-out: restore the server version and release the lock.", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var op = await session.PrepareUndoCheckOutAsync(targets, parse.GetValue(discard), ConsoleProgress(), ct);
            await ApplyAndReport(session, op, ct);
        }, paths, discard);
    }

    private static Command HistoryCommand()
    {
        var path = new Argument<string>("path") { Description = "File." };
        return Define("history", "Show a file's versions.", async (parse, manager, ct) =>
        {
            var full = Path.GetFullPath(parse.GetValue(path)!);
            var session = await VaultFor(parse, manager, new[] { full }, ct);
            foreach (var v in await session.GetHistoryAsync(full, ct))
                Console.WriteLine($"v{v.Version,-4} {v.At,-21} {v.By,-14} {(v.State ?? "").PadRight(9)} {(v.Revision ?? "").PadRight(3)} {v.Comment}");
        }, path);
    }

    private static Command GetVersionCommand()
    {
        var path = new Argument<string>("path") { Description = "File." };
        var version = new Argument<int>("version") { Description = "Version number." };
        var asBuilt = new Option<bool>("--as-built") { Description = "Also restore the referenced files at the versions used then." };
        return Define("getver", "Put an older version in your vault folder (read-only).", async (parse, manager, ct) =>
        {
            var full = Path.GetFullPath(parse.GetValue(path)!);
            var session = await VaultFor(parse, manager, new[] { full }, ct);
            var op = await session.PrepareGetVersionAsync(full, parse.GetValue(version), parse.GetValue(asBuilt), false, ConsoleProgress(), ct);
            await ApplyAndReport(session, op, ct);
        }, path, version, asBuilt);
    }

    private static Command RollbackCommand()
    {
        var path = new Argument<string>("path") { Description = "Checked-out file." };
        var version = new Argument<int>("version") { Description = "Version to bring back." };
        return Define("rollback", "Replace a checked-out file with an older version (check in to make it the newest).", async (parse, manager, ct) =>
        {
            var full = Path.GetFullPath(parse.GetValue(path)!);
            var session = await VaultFor(parse, manager, new[] { full }, ct);
            var op = await session.PrepareRollbackAsync(full, parse.GetValue(version), ConsoleProgress(), ct);
            await ApplyAndReport(session, op, ct);
            Console.WriteLine("Check the file in to save it as a new version.");
        }, path, version);
    }

    private static Command TransitionCommand()
    {
        var paths = PathsArgument();
        var name = new Option<string?>("--to") { Description = "Transition name, e.g. \"Submit for review\". Omit to list the options." };
        var message = new Option<string?>("-m", "--message") { Description = "Comment." };
        return Define("transition", "Change workflow state (submit, approve, reject, change request...).", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var transition = parse.GetValue(name);
            if (transition == null)
            {
                foreach (var option in await session.GetTransitionsAsync(targets[0], ct))
                    Console.WriteLine($"{option.Name,-20} -> {option.To,-10} {(option.Allowed ? "" : "(" + option.Reason + ")")}{(option.NextRevision != null ? " rev " + option.NextRevision : "")}");
                return;
            }
            var (commit, warnings) = await session.TransitionAsync(targets, transition, parse.GetValue(message), ct);
            Console.WriteLine(commit == null ? "Nothing changed." : $"Done ({commit[..8]}).");
            PrintWarnings(warnings);
        }, paths, name, message);
    }

    private static Command DeleteCommand()
    {
        var paths = PathsArgument();
        var message = new Option<string?>("-m", "--message") { Description = "Comment." };
        var force = new Option<bool>("--force") { Description = "Admin: delete even though other files reference it." };
        return Define("delete", "Delete files from the vault (history is kept).", async (parse, manager, ct) =>
        {
            var targets = FullPaths(parse.GetValue(paths));
            var session = await VaultFor(parse, manager, targets, ct);
            var (_, warnings) = await session.DeleteAsync(targets, parse.GetValue(message), parse.GetValue(force), ct);
            Console.WriteLine("Deleted.");
            PrintWarnings(warnings);
        }, paths, message, force);
    }

    private static Command LocksCommand() =>
        Define("locks", "List everything that is checked out.", async (parse, manager, ct) =>
        {
            var session = await VaultFor(parse, manager, Array.Empty<string>(), ct);
            await session.SyncAsync(fetch: false, refreshLocks: true, ct);
            foreach (var l in session.GetLocks()) Console.WriteLine($"{l.Path,-50} {l.Owner,-16} {l.LockedAt}");
        });

    private static Command UnlockCommand()
    {
        var path = new Argument<string>("path") { Description = "File (vault path or local path)." };
        return Define("unlock", "Admin: release someone else's check-out.", async (parse, manager, ct) =>
        {
            var value = parse.GetValue(path)!;
            var session = await VaultFor(parse, manager, Path.IsPathFullyQualified(value) ? new[] { value } : Array.Empty<string>(), ct);
            await session.ForceUnlockAsync(value, ct);
            Console.WriteLine("Released.");
        }, path);
    }

    private static Command RefsCommand()
    {
        var path = new Argument<string>("path") { Description = "File." };
        var up = new Option<bool>("--where-used") { Description = "Show what uses the file instead of what it contains." };
        return Define("refs", "Show an assembly/drawing's references (or where a part is used).", async (parse, manager, ct) =>
        {
            var full = Path.GetFullPath(parse.GetValue(path)!);
            var session = await VaultFor(parse, manager, new[] { full }, ct);
            await session.TrySyncAsync(refreshLocks: false, ct);
            if (parse.GetValue(up))
            {
                foreach (var parent in session.WhereUsed(full)) Console.WriteLine(parent);
                return;
            }
            var tree = await session.GetReferenceTreeAsync(full, recursive: true, ct);
            void Print(ReferenceNodeDto node, int depth)
            {
                Console.WriteLine($"{new string(' ', depth * 2)}{node.Path} (v{node.Version})  {(node.Status == null ? "" : Describe(node.Status))}");
                foreach (var child in node.Children ?? Array.Empty<ReferenceNodeDto>()) Print(child, depth + 1);
            }
            if (tree != null) Print(tree, 0);
        }, path, up);
    }

    private static Command ImportCommand()
    {
        var folder = new Argument<string>("folder") { Description = "Folder inside the vault whose new files should be imported." };
        var message = new Option<string?>("-m", "--message") { Description = "Comment." };
        return Define("import", "Bulk check-in of new files already copied into the vault folder (use the SOLIDWORKS import wizard to also fix references).", async (parse, manager, ct) =>
        {
            var full = Path.GetFullPath(parse.GetValue(folder)!);
            var session = await VaultFor(parse, manager, new[] { full }, ct);
            await session.SyncAsync(ct: ct);
            var vaultFolder = SwVault.Core.Util.PathRules.ToVaultPath(session.LocalRoot, full) ?? "";
            var newFiles = session.EnumerateLocalOnly(vaultFolder).ToList();
            Console.WriteLine($"Importing {newFiles.Count} new files...");
            var result = await session.CheckInAsync(newFiles, null, new CheckInOptions
            {
                IsImport = true,
                AllowMissingReferences = true,
                Comment = parse.GetValue(message) ?? $"Import {vaultFolder}",
            }, ConsoleProgress(), ct);
            Console.WriteLine($"Imported {result.NewVersions.Count} files.");
            PrintWarnings(result.Warnings);
        }, folder, message);
    }

    private static Command DoctorCommand() =>
        Define("doctor", "Check this PC's setup (git, sign-in, vault folders, SOLIDWORKS).", async (parse, manager, ct) =>
        {
            await Doctor.RunAsync(manager, ct);
        });
}
