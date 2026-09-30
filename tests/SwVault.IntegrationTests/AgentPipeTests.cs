using System.Collections.Concurrent;
using SwVault.Agent;
using SwVault.Core.Auth;
using SwVault.Core.Client;
using SwVault.Core.Hosts;
using SwVault.Protocol;

namespace SwVault.IntegrationTests;

/// <summary>The add-in's view of the world: talk to a real agent over its named pipe.</summary>
public class AgentPipeTests
{
    [Fact]
    public async Task AddInStyleFlow_OverThePipe()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        TestVault.Write(alice, "Parts/Bolt.SLDPRT", "bolt v1");
        await alice.CheckInPathsAsync("v1", "Parts/Bolt.SLDPRT");

        var profile = new SwVaultProfile("test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var agent = new AgentHost(profile);
            agent.Vaults.ConfigureSession = o => o with
            {
                Credentials = new StaticCredentialProvider(new Credential("bob", "secret")),
                Host = new StaticHostAdapter(new HostUser("bob", "Bob", "bob@example.com")),
                LfsUrl = new Uri(vault.Server.LfsUrl()),
                AllowSyncedFolderRoot = true,
            };
            var bobRoot = Path.Combine(vault.Root, "ws-bob-agent");
            await agent.Vaults.AddAsync(vault.RemotePath, bobRoot, null);
            agent.Start();

            using var client = new AgentClient();
            var notifications = new ConcurrentQueue<string>();
            client.NotificationReceived += (method, _) => notifications.Enqueue(method);
            await client.ConnectAsync(profile.PipeName, "tests", "1.0", 5000, CancellationToken.None);
            Assert.Equal(ProtocolInfo.Version, client.Agent.ProtocolVersion);

            var vaults = await client.CallAsync<object?, VaultInfo[]>(Methods.VaultsList, null);
            var info = Assert.Single(vaults);
            Assert.Equal("bob", info.UserLogin);

            // Nothing local yet: get latest applies immediately.
            var job = await client.CallAsync<JobRequest, JobInfo>(Methods.JobStart, new JobRequest { VaultId = info.Id, Kind = JobKind.GetLatest, Paths = new[] { bobRoot } });
            Assert.Equal(JobState.Completed, job.State);
            var bolt = Path.Combine(bobRoot, "Parts", "Bolt.SLDPRT");
            Assert.Equal("bolt v1", File.ReadAllText(bolt));

            var statuses = await client.CallAsync<PathsRequest, FileStatusDto[]>(Methods.StatusGet, new PathsRequest { Paths = new[] { bolt } });
            Assert.Equal(LocalState.UpToDate, Assert.Single(statuses).LocalState);

            job = await client.CallAsync<JobRequest, JobInfo>(Methods.JobStart, new JobRequest { Kind = JobKind.CheckOut, Paths = new[] { bolt } });
            Assert.Equal(JobState.Completed, job.State);
            Assert.False(File.GetAttributes(bolt).HasFlag(FileAttributes.ReadOnly));

            File.WriteAllText(bolt, "bolt v2");
            job = await client.CallAsync<JobRequest, JobInfo>(Methods.JobStart, new JobRequest
            {
                Kind = JobKind.CheckIn,
                Paths = new[] { bolt },
                Comment = "Longer bolt",
                Files = new[] { new CheckInFileInfo { LocalPath = bolt, Properties = new() { ["PartNo"] = "B-1" }, SwVersion = "2025 SP3" } },
            });
            Assert.Equal(JobState.Completed, job.State);
            Assert.NotNull(job.Commit);

            // Someone else checks in v3; now bob's copy exists, so the job waits for job.apply.
            await alice.CheckOutAndApplyAsync("Parts/Bolt.SLDPRT");
            TestVault.Write(alice, "Parts/Bolt.SLDPRT", "bolt v3");
            await alice.CheckInPathsAsync("v3", "Parts/Bolt.SLDPRT");
            job = await client.CallAsync<JobRequest, JobInfo>(Methods.JobStart, new JobRequest { Kind = JobKind.GetLatest, Paths = new[] { bolt } });
            Assert.Equal(JobState.ReadyToApply, job.State);
            Assert.Contains(bolt, job.Replacements, StringComparer.OrdinalIgnoreCase);
            job = await client.CallAsync<JobIdRequest, JobInfo>(Methods.JobApply, new JobIdRequest { JobId = job.JobId });
            Assert.Equal(JobState.Completed, job.State);
            Assert.Equal("bolt v3", File.ReadAllText(bolt));

            var history = await client.CallAsync<PathsRequest, VersionInfoDto[]>(Methods.History, new PathsRequest { Paths = new[] { bolt } });
            Assert.Equal(new[] { 3, 2, 1 }, history.Select(h => h.Version).ToArray());

            var outside = await Assert.ThrowsAsync<AgentException>(() =>
                client.CallAsync<PathsRequest, VersionInfoDto[]>(Methods.History, new PathsRequest { Paths = new[] { @"C:\NotAVault\x.sldprt" } }));
            Assert.Equal(ErrorCodes.NotFound, outside.Code);

            var failed = await client.CallAsync<JobRequest, JobInfo>(Methods.JobStart, new JobRequest { Kind = JobKind.CheckIn, Paths = new[] { bolt } });
            Assert.Equal(JobState.Failed, failed.State);
            Assert.Equal(ErrorCodes.Conflict, failed.ErrorCode);

            await Task.Delay(200);
            Assert.Contains(Notifications.JobUpdated, notifications);
            Assert.Contains(Notifications.StatusChanged, notifications);
        }
        finally
        {
            try { Directory.Delete(profile.BaseDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
