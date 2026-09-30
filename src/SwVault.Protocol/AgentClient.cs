using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SwVault.Protocol
{
    /// <summary>An error reported by the agent (or a lost connection, with code 503).</summary>
    public sealed class AgentException : Exception
    {
        public int Code { get; }

        public AgentException(int code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// Client side of the agent pipe: request/response by id, plus notifications. Used by the
    /// SOLIDWORKS add-in (net48) and by tests. Continuations never capture the caller's
    /// synchronization context, so callers must marshal notification handlers themselves.
    /// </summary>
    public sealed class AgentClient : IDisposable
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>>();
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private long _nextId;
        private int _disconnected;

        /// <summary>(method, payload JSON) for every agent notification. Raised on a background thread.</summary>
        public event Action<string, string> NotificationReceived;

        /// <summary>Raised once when the pipe closes (agent exited or crashed).</summary>
        public event Action Disconnected;

        public bool IsConnected => _pipe != null && _pipe.IsConnected && Volatile.Read(ref _disconnected) == 0;

        public HelloResponse Agent { get; private set; }

        public async Task ConnectAsync(string pipeName, string clientName, string clientVersion, int timeoutMilliseconds, CancellationToken ct)
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(timeoutMilliseconds, ct).ConfigureAwait(false);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var reader = new StreamReader(_pipe, new UTF8Encoding(false));
            var _ = Task.Run(() => ReadLoopAsync(reader));
            Agent = await CallAsync<HelloRequest, HelloResponse>(Methods.Hello, new HelloRequest
            {
                ProtocolVersion = ProtocolInfo.Version,
                ClientName = clientName,
                ClientVersion = clientVersion,
                ProcessId = Process.GetCurrentProcess().Id,
            }, ct).ConfigureAwait(false);
        }

        public async Task<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken ct = default(CancellationToken))
        {
            if (_writer == null) throw new AgentException(ErrorCodes.Offline, "Not connected to the SwVault agent.");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await SendAsync(new RpcMessage { Id = id, Method = method, Payload = WireJson.Serialize(request) }).ConfigureAwait(false);
                using (ct.Register(() => completion.TrySetCanceled()))
                {
                    var response = await completion.Task.ConfigureAwait(false);
                    if (response.Error != null) throw new AgentException(response.Error.Code, response.Error.Message);
                    return WireJson.Deserialize<TResponse>(response.Payload);
                }
            }
            finally
            {
                TaskCompletionSource<RpcMessage> ignored;
                _pending.TryRemove(id, out ignored);
            }
        }

        private async Task SendAsync(RpcMessage message)
        {
            var line = WireJson.Serialize(message);
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                OnDisconnected();
                throw new AgentException(ErrorCodes.Offline, "Lost the connection to the SwVault agent: " + ex.Message);
            }
            catch (ObjectDisposedException)
            {
                throw new AgentException(ErrorCodes.Offline, "Not connected to the SwVault agent.");
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task ReadLoopAsync(StreamReader reader)
        {
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    RpcMessage message;
                    try
                    {
                        message = WireJson.Deserialize<RpcMessage>(line);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (message == null) continue;
                    if (message.IsResponse)
                    {
                        TaskCompletionSource<RpcMessage> completion;
                        if (_pending.TryRemove(message.Id, out completion)) completion.TrySetResult(message);
                    }
                    else if (message.IsNotification)
                    {
                        try
                        {
                            NotificationReceived?.Invoke(message.Method, message.Payload);
                        }
                        catch (Exception)
                        {
                            // A faulty handler must not kill the read loop.
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            OnDisconnected();
        }

        private void OnDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new AgentException(ErrorCodes.Offline, "The SwVault agent closed the connection."));
            Disconnected?.Invoke();
        }

        public void Dispose()
        {
            try
            {
                _pipe?.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
