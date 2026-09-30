using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SwVault.Protocol;

namespace SwVault.Agent;

/// <summary>
/// Named-pipe server for the add-in and other local clients. One line of JSON per message;
/// the pipe ACL only admits the current Windows user.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<ClientConnection, RpcMessage, Task<RpcMessage>> _dispatch;
    private readonly FileLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ClientConnection> _clients = new();
    private Task? _acceptLoop;

    public PipeServer(string pipeName, Func<ClientConnection, RpcMessage, Task<RpcMessage>> dispatch, FileLog log)
    {
        _pipeName = pipeName;
        _dispatch = dispatch;
        _log = log;
    }

    public int ClientCount
    {
        get
        {
            lock (_clients) return _clients.Count;
        }
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Pipe accept failed", ex);
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }

            var connection = new ClientConnection(pipe, _dispatch, _log, OnDisconnected);
            lock (_clients) _clients.Add(connection);
            _ = connection.RunAsync(_cts.Token);
        }
    }

    private void OnDisconnected(ClientConnection connection)
    {
        lock (_clients) _clients.Remove(connection);
    }

    public void Broadcast(string method, object payload)
    {
        var message = new RpcMessage { Method = method, Payload = WireJson.SerializeObject(payload) };
        List<ClientConnection> clients;
        lock (_clients) clients = _clients.ToList();
        foreach (var client in clients) _ = client.SendAsync(message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        List<ClientConnection> clients;
        lock (_clients) clients = _clients.ToList();
        foreach (var client in clients) client.Dispose();
        try { _acceptLoop?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}

internal sealed class ClientConnection : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Func<ClientConnection, RpcMessage, Task<RpcMessage>> _dispatch;
    private readonly FileLog _log;
    private readonly Action<ClientConnection> _onDisconnected;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StreamWriter _writer;

    public string ClientName { get; set; } = "unknown";
    public int ProcessId { get; set; }

    public ClientConnection(NamedPipeServerStream pipe, Func<ClientConnection, RpcMessage, Task<RpcMessage>> dispatch, FileLog log, Action<ClientConnection> onDisconnected)
    {
        _pipe = pipe;
        _dispatch = dispatch;
        _log = log;
        _onDisconnected = onDisconnected;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                RpcMessage? request;
                try
                {
                    request = WireJson.Deserialize<RpcMessage>(line);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Bad message from {ClientName}: {ex.Message}");
                    continue;
                }
                if (request is not { IsRequest: true }) continue;
                // Requests run concurrently: a long job must not block status queries on the same pipe.
                _ = Task.Run(async () =>
                {
                    var response = await _dispatch(this, request).ConfigureAwait(false);
                    response.Id = request.Id;
                    await SendAsync(response).ConfigureAwait(false);
                }, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _onDisconnected(this);
            Dispose();
        }
    }

    public async Task SendAsync(RpcMessage message)
    {
        var line = WireJson.Serialize(message);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe.IsConnected) await _writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        try { _pipe.Dispose(); } catch (IOException) { }
    }
}
