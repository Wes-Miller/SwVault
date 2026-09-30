using SwVault.Core;
using SwVault.Core.Auth;
using SwVault.Core.Client;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>Maps pipe requests to vault operations.</summary>
internal sealed class RpcDispatcher
{
    private readonly VaultManager _vaults;
    private readonly JobManager _jobs;
    private readonly FileLog _log;
    private readonly Action? _onVaultsChanged;
    private readonly TeamConfig? _team;
    private readonly Func<string, string, Task<VaultSession>>? _joinTeam;
    private readonly Action<string>? _showWindow;

    public RpcDispatcher(VaultManager vaults, JobManager jobs, FileLog log, Action? onVaultsChanged = null,
        TeamConfig? team = null, Func<string, string, Task<VaultSession>>? joinTeam = null, Action<string>? showWindow = null)
    {
        _showWindow = showWindow;
        _vaults = vaults;
        _jobs = jobs;
        _log = log;
        _onVaultsChanged = onVaultsChanged;
        _team = team;
        _joinTeam = joinTeam;
    }

    public async Task<RpcMessage> DispatchAsync(ClientConnection client, RpcMessage request)
    {
        try
        {
            var result = await HandleAsync(client, request.Method!, request.Payload).ConfigureAwait(false);
            return new RpcMessage { Payload = result == null ? null : WireJson.SerializeObject(result) };
        }
        catch (VaultException ex)
        {
            return new RpcMessage { Error = new RpcError { Code = ex.Code, Message = ex.Message } };
        }
        catch (Exception ex)
        {
            _log.Error($"{request.Method} failed", ex);
            return new RpcMessage { Error = new RpcError { Code = ErrorCodes.Internal, Message = "Unexpected error in the SwVault agent: " + ex.Message } };
        }
    }

    private static T Payload<T>(string? json) where T : class =>
        WireJson.Deserialize<T>(json) ?? throw VaultException.BadRequest($"Missing {typeof(T).Name}.");

    private async Task<object?> HandleAsync(ClientConnection client, string method, string? payload)
    {
        switch (method)
        {
            case Methods.Hello:
            {
                var hello = Payload<HelloRequest>(payload);
                client.ClientName = hello.ClientName ?? "unknown";
                client.ProcessId = hello.ProcessId;
                _log.Info($"Client connected: {client.ClientName} {hello.ClientVersion} (pid {hello.ProcessId}, protocol {hello.ProtocolVersion})");
                if (hello.ProtocolVersion != ProtocolInfo.Version)
                    throw new VaultException(ErrorCodes.VersionMismatch, $"The SwVault agent speaks protocol {ProtocolInfo.Version} but {client.ClientName} speaks {hello.ProtocolVersion}. Reinstall SwVault so both match.");
                return new HelloResponse { ProtocolVersion = ProtocolInfo.Version, AgentVersion = typeof(RpcDispatcher).Assembly.GetName().Version?.ToString(), AgentProcessId = Environment.ProcessId };
            }

            case Methods.VaultsList:
            {
                var list = new List<VaultInfo>();
                foreach (var registration in _vaults.Registrations)
                {
                    try
                    {
                        list.Add(Describe(await _vaults.GetAsync(registration.Id).ConfigureAwait(false)));
                    }
                    catch (VaultException ex)
                    {
                        list.Add(new VaultInfo { Id = registration.Id, Name = registration.Name, RemoteUrl = registration.RemoteUrl, LocalRoot = registration.LocalRoot, LastError = ex.Message });
                    }
                }
                return list.ToArray();
            }

            case Methods.VaultAdd:
            {
                var add = Payload<VaultAddRequest>(payload);
                VaultSession session;
                var isTeamVault = _team != null && (string.IsNullOrWhiteSpace(add.RemoteUrl) || string.Equals(add.RemoteUrl.Trim(), _team.VaultUrl, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(add.Password) && isTeamVault && _joinTeam != null)
                {
                    session = await _joinTeam(add.UserName ?? "", add.Password).ConfigureAwait(false);
                }
                else
                {
                    var secret = add.Token;
                    if (!string.IsNullOrEmpty(add.Password) && !string.IsNullOrEmpty(add.UserName))
                        secret = await TeamJoin.CreateTokenAsync(new Uri(add.RemoteUrl), add.UserName, add.Password, CancellationToken.None).ConfigureAwait(false);
                    var credential = !string.IsNullOrEmpty(add.UserName) && !string.IsNullOrEmpty(secret) ? new Credential(add.UserName, secret) : null;
                    session = await _vaults.AddAsync(add.RemoteUrl, string.IsNullOrWhiteSpace(add.LocalRoot) ? null : add.LocalRoot, credential).ConfigureAwait(false);
                }
                _onVaultsChanged?.Invoke();
                return Describe(session);
            }

            case Methods.UiShow:
            {
                var show = Payload<UiShowRequest>(payload);
                if (show.What is not ("signIn" or "invite" or "profile" or "reviews" or "requestReview"))
                    throw VaultException.BadRequest($"Unknown window '{show.What}'.");
                if (show.What == "requestReview" && string.IsNullOrEmpty(show.Path)) throw VaultException.BadRequest("Which file?");
                _showWindow?.Invoke(show.What == "requestReview" ? "requestReview|" + show.Path : show.What);
                return null;
            }

            case Methods.TeamGet:
                return _team == null ? null : new TeamInfo { Name = _team.Name, VaultUrl = _team.VaultUrl, LocalRoot = _team.LocalRoot };

            case Methods.VaultSync:
            {
                var session = await _vaults.GetAsync(Payload<VaultRequest>(payload).VaultId).ConfigureAwait(false);
                await session.TrySyncAsync().ConfigureAwait(false);
                return Describe(session);
            }

            case Methods.StatusGet:
            {
                var request = Payload<PathsRequest>(payload);
                var result = new List<FileStatusDto>();
                foreach (var path in request.Paths ?? Array.Empty<string>())
                {
                    var session = await SessionForAsync(request.VaultId, path).ConfigureAwait(false);
                    if (session == null) continue;
                    var status = await session.GetStatusAsync(path).ConfigureAwait(false);
                    if (status != null) result.Add(status);
                }
                return result.ToArray();
            }

            case Methods.StatusFolder:
            {
                var request = Payload<FolderRequest>(payload);
                var session = await _vaults.GetAsync(request.VaultId).ConfigureAwait(false);
                return (await session.ListFolderAsync(request.Folder ?? "").ConfigureAwait(false)).ToArray();
            }

            case Methods.Search:
            {
                var request = Payload<SearchRequest>(payload);
                var session = await _vaults.GetAsync(request.VaultId).ConfigureAwait(false);
                return (await session.SearchAsync(request.Query, request.Filter, request.Max).ConfigureAwait(false)).ToArray();
            }

            case Methods.History:
            {
                var (session, path) = await SingleAsync(payload).ConfigureAwait(false);
                return (await session.GetHistoryAsync(path).ConfigureAwait(false)).ToArray();
            }

            case Methods.References:
            {
                var (session, path) = await SingleAsync(payload).ConfigureAwait(false);
                return await session.GetReferenceTreeAsync(path, recursive: true).ConfigureAwait(false);
            }

            case Methods.WhereUsed:
            {
                var (session, path) = await SingleAsync(payload).ConfigureAwait(false);
                return session.WhereUsed(path).Select(session.LocalPathOf).ToArray();
            }

            case Methods.Transitions:
            {
                var (session, path) = await SingleAsync(payload).ConfigureAwait(false);
                return (await session.GetTransitionsAsync(path).ConfigureAwait(false)).ToArray();
            }

            case Methods.Locks:
            {
                var session = await _vaults.GetAsync(Payload<VaultRequest>(payload).VaultId).ConfigureAwait(false);
                return session.GetLocks().ToArray();
            }

            case Methods.JobStart:
                return await _jobs.StartAsync(Payload<JobRequest>(payload)).ConfigureAwait(false);

            case Methods.JobApply:
            {
                var apply = Payload<JobIdRequest>(payload);
                return await _jobs.ApplyAsync(apply.JobId, apply.SkipPaths).ConfigureAwait(false);
            }

            case Methods.JobCancel:
                return _jobs.Cancel(Payload<JobIdRequest>(payload).JobId);

            case Methods.JobGet:
                return _jobs.Get(Payload<JobIdRequest>(payload).JobId);

            default:
                throw VaultException.BadRequest($"Unknown method '{method}'.");
        }
    }

    private async Task<VaultSession?> SessionForAsync(string? vaultId, string path)
    {
        if (!string.IsNullOrEmpty(vaultId)) return await _vaults.GetAsync(vaultId).ConfigureAwait(false);
        return Path.IsPathFullyQualified(path) ? await _vaults.FindByLocalPathAsync(path).ConfigureAwait(false) : null;
    }

    private async Task<(VaultSession Session, string Path)> SingleAsync(string? payload)
    {
        var request = Payload<PathsRequest>(payload);
        var path = request.Paths?.FirstOrDefault() ?? throw VaultException.BadRequest("Missing path.");
        var session = await SessionForAsync(request.VaultId, path).ConfigureAwait(false)
            ?? throw VaultException.NotFound($"{path} is not inside a vault folder.");
        return (session, path);
    }

    internal static VaultInfo Describe(VaultSession session) => new()
    {
        Id = session.VaultId,
        Name = session.Config.Name,
        RemoteUrl = session.RemoteUrl,
        LocalRoot = session.LocalRoot,
        UserLogin = session.User?.Login,
        UserDisplayName = session.User?.DisplayName,
        Online = session.Online,
        LastSyncUtc = session.LastSyncUtc?.ToString("O"),
        HeadCommit = session.Head.Commit,
        SolidworksVersion = session.Config.SolidworksVersion,
        Roles = session.Config.RolesOf(session.User?.Login).ToArray(),
        LastError = session.LastError,
    };
}
