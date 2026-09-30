using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SwVault.AddIn.Infrastructure;
using SwVault.Protocol;

namespace SwVault.AddIn
{
    /// <summary>
    /// The add-in's link to the SwVault agent: starts the agent when it isn't running, reconnects
    /// after it restarts, and raises agent notifications on SOLIDWORKS' main thread.
    /// </summary>
    internal sealed class AgentConnection : IDisposable
    {
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private readonly string _profile;
        private readonly string _pipeName;
        private AgentClient _client;

        public event Action<StatusChangedNotification> StatusChanged;
        public event Action<JobInfo> JobUpdated;
        public event Action<ToastNotification> Toast;
        public event Action ConnectionChanged;

        public AgentConnection()
        {
            _profile = Environment.GetEnvironmentVariable("SWVAULT_PROFILE");
            if (string.IsNullOrWhiteSpace(_profile)) _profile = "default";
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            _pipeName = ProtocolInfo.PipeName(sid) + (_profile == "default" ? "" : "." + _profile);
        }

        public bool IsConnected => _client != null && _client.IsConnected;

        public async Task EnsureConnectedAsync()
        {
            if (IsConnected) return;
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsConnected) return;
                if (await TryConnectAsync(700).ConfigureAwait(false)) return;
                LaunchAgent();
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    if (await TryConnectAsync(1000).ConfigureAwait(false)) return;
                    await Task.Delay(400).ConfigureAwait(false);
                }
                throw new AgentException(ErrorCodes.Offline, "The SwVault agent is not running and could not be started. Reinstall SwVault or start SwVault.Agent.exe.");
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private async Task<bool> TryConnectAsync(int timeoutMilliseconds)
        {
            var client = new AgentClient();
            try
            {
                await client.ConnectAsync(_pipeName, "SOLIDWORKS add-in", typeof(AgentConnection).Assembly.GetName().Version.ToString(), timeoutMilliseconds, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.VersionMismatch)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is AgentException || ex is UnauthorizedAccessException)
            {
                client.Dispose();
                return false;
            }

            client.NotificationReceived += OnNotification;
            client.Disconnected += () => UiThread.Post(() => ConnectionChanged?.Invoke());
            var old = _client;
            _client = client;
            old?.Dispose();
            Log.Info("Connected to the SwVault agent " + client.Agent?.AgentVersion);
            UiThread.Post(() => ConnectionChanged?.Invoke());
            return true;
        }

        private void LaunchAgent()
        {
            var path = AgentPath();
            if (path == null || !File.Exists(path))
            {
                Log.Warn("SwVault.Agent.exe not found (looked in HKCU\\Software\\SwVault and next to the add-in).");
                return;
            }
            try
            {
                var args = _profile == "default" ? "" : "--profile " + _profile;
                Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) });
                Log.Info("Started agent " + path);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the agent", ex);
            }
        }

        private string AgentPath()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\SwVault"))
            {
                var registered = key?.GetValue(_profile == "default" ? "AgentPath" : "AgentPath." + _profile) as string;
                if (!string.IsNullOrEmpty(registered) && File.Exists(registered)) return registered;
            }
            // Installed layout: <install>\addin\SwVault.AddIn.dll and <install>\bin\SwVault.Agent.exe.
            var addInDir = Path.GetDirectoryName(typeof(AgentConnection).Assembly.Location) ?? "";
            foreach (var candidate in new[] { Path.Combine(addInDir, "SwVault.Agent.exe"), Path.GetFullPath(Path.Combine(addInDir, "..", "bin", "SwVault.Agent.exe")) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private void OnNotification(string method, string payload)
        {
            try
            {
                switch (method)
                {
                    case Notifications.StatusChanged:
                        var status = WireJson.Deserialize<StatusChangedNotification>(payload);
                        UiThread.Post(() => StatusChanged?.Invoke(status));
                        break;
                    case Notifications.JobUpdated:
                        var job = WireJson.Deserialize<JobInfo>(payload);
                        UiThread.Post(() => JobUpdated?.Invoke(job));
                        break;
                    case Notifications.Toast:
                        var toast = WireJson.Deserialize<ToastNotification>(payload);
                        UiThread.Post(() => Toast?.Invoke(toast));
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Bad notification " + method + ": " + ex.Message);
            }
        }

        public async Task<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request)
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            try
            {
                return await _client.CallAsync<TRequest, TResponse>(method, request).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.Offline && method != Methods.JobStart && method != Methods.JobApply)
            {
                // The agent restarted; read-only calls are safe to retry once.
                await EnsureConnectedAsync().ConfigureAwait(false);
                return await _client.CallAsync<TRequest, TResponse>(method, request).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------ typed calls

        public Task<VaultInfo[]> GetVaultsAsync() => CallAsync<object, VaultInfo[]>(Methods.VaultsList, null);

        /// <summary>The team vault this install was packaged for, or null.</summary>
        public Task<TeamInfo> GetTeamAsync() => CallAsync<object, TeamInfo>(Methods.TeamGet, null);

        public Task<VaultInfo> AddVaultAsync(VaultAddRequest request) => CallAsync<VaultAddRequest, VaultInfo>(Methods.VaultAdd, request);

        public Task<VaultInfo> SyncVaultAsync(string vaultId) => CallAsync<VaultRequest, VaultInfo>(Methods.VaultSync, new VaultRequest { VaultId = vaultId });

        public Task<FileStatusDto[]> GetStatusAsync(params string[] paths) => CallAsync<PathsRequest, FileStatusDto[]>(Methods.StatusGet, new PathsRequest { Paths = paths });

        public Task<FileStatusDto[]> ListFolderAsync(string vaultId, string folder) =>
            CallAsync<FolderRequest, FileStatusDto[]>(Methods.StatusFolder, new FolderRequest { VaultId = vaultId, Folder = folder });

        public Task<FileStatusDto[]> SearchAsync(string vaultId, string query, StatusFilter filter) =>
            CallAsync<SearchRequest, FileStatusDto[]>(Methods.Search, new SearchRequest { VaultId = vaultId, Query = query, Filter = filter, Max = 500 });

        public Task<VersionInfoDto[]> HistoryAsync(string path) => CallAsync<PathsRequest, VersionInfoDto[]>(Methods.History, new PathsRequest { Paths = new[] { path } });

        public Task<ReferenceNodeDto> ReferencesAsync(string path) => CallAsync<PathsRequest, ReferenceNodeDto>(Methods.References, new PathsRequest { Paths = new[] { path } });

        public Task<string[]> WhereUsedAsync(string path) => CallAsync<PathsRequest, string[]>(Methods.WhereUsed, new PathsRequest { Paths = new[] { path } });

        public Task<TransitionOptionDto[]> TransitionsAsync(string path) => CallAsync<PathsRequest, TransitionOptionDto[]>(Methods.Transitions, new PathsRequest { Paths = new[] { path } });

        public Task<LockDto[]> LocksAsync(string vaultId) => CallAsync<VaultRequest, LockDto[]>(Methods.Locks, new VaultRequest { VaultId = vaultId });

        public Task<JobInfo> StartJobAsync(JobRequest request) => CallAsync<JobRequest, JobInfo>(Methods.JobStart, request);

        public Task<JobInfo> ApplyJobAsync(string jobId, string[] skipPaths) =>
            CallAsync<JobIdRequest, JobInfo>(Methods.JobApply, new JobIdRequest { JobId = jobId, SkipPaths = skipPaths });

        public Task<JobInfo> CancelJobAsync(string jobId) => CallAsync<JobIdRequest, JobInfo>(Methods.JobCancel, new JobIdRequest { JobId = jobId });

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
