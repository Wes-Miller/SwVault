using SwVault.Core;
using SwVault.Protocol;
using static SwVault.IntegrationTests.TestVault;

namespace SwVault.IntegrationTests;

public class CheckInOutTests
{
    [Fact]
    public async Task NewFileCheckIn_IsVisibleToOthers_AndReadOnly()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");

        Write(alice, "Chassis/Frame.SLDPRT", "frame v1");
        Assert.Equal(LocalState.LocalOnly, (await StatusAsync(alice, "Chassis/Frame.SLDPRT")).LocalState);

        var result = await alice.CheckInPathsAsync("First frame", "Chassis/Frame.SLDPRT");
        Assert.NotNull(result.Commit);
        Assert.True(IsReadOnly(alice, "Chassis/Frame.SLDPRT"));
        Assert.Empty(vault.Server.CurrentLocks);

        var aliceStatus = await StatusAsync(alice, "Chassis/Frame.SLDPRT");
        Assert.Equal(LocalState.UpToDate, aliceStatus.LocalState);
        Assert.Equal(1, aliceStatus.ServerVersion);
        Assert.Equal("WIP", aliceStatus.State);
        Assert.Equal("First frame", aliceStatus.Comment);

        await bob.SyncAsync();
        Assert.Equal(LocalState.NotLocal, (await StatusAsync(bob, "Chassis/Frame.SLDPRT")).LocalState);
        var applied = await bob.GetLatestAsync(false, "Chassis");
        Assert.Single(applied.Applied);
        Assert.Equal("frame v1", Read(bob, "Chassis/Frame.SLDPRT"));
        Assert.True(IsReadOnly(bob, "Chassis/Frame.SLDPRT"));
        Assert.Equal(LocalState.UpToDate, (await StatusAsync(bob, "Chassis/Frame.SLDPRT")).LocalState);
    }

    [Fact]
    public async Task CheckOut_LocksForOthers_AndCheckInCreatesVersion2()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        Write(alice, "Upright.SLDPRT", "upright v1");
        await alice.CheckInPathsAsync("v1", "Upright.SLDPRT");

        await bob.CheckOutAndApplyAsync("Upright.SLDPRT");
        Assert.False(IsReadOnly(bob, "Upright.SLDPRT"));
        Assert.Equal("upright v1", Read(bob, "Upright.SLDPRT"));
        Assert.Equal(LockState.MineHere, (await StatusAsync(bob, "Upright.SLDPRT")).LockState);

        var ex = await Assert.ThrowsAsync<VaultException>(() => alice.CheckOutAsync(new[] { "Upright.SLDPRT" }));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);
        Assert.Contains("bob", ex.Message);
        var aliceView = await StatusAsync(alice, "Upright.SLDPRT");
        Assert.Equal(LockState.Other, aliceView.LockState);
        Assert.Equal("bob", aliceView.LockOwner);

        Write(bob, "Upright.SLDPRT", "upright v2");
        Assert.Equal(LocalState.Modified, (await StatusAsync(bob, "Upright.SLDPRT")).LocalState);
        var checkIn = await bob.CheckInPathsAsync("Lighter upright", "Upright.SLDPRT");
        Assert.Contains("Upright.SLDPRT v2", checkIn.NewVersions);
        Assert.True(IsReadOnly(bob, "Upright.SLDPRT"));

        await alice.SyncAsync();
        var outdated = await StatusAsync(alice, "Upright.SLDPRT");
        Assert.Equal(LocalState.Outdated, outdated.LocalState);
        Assert.Equal(1, outdated.LocalVersion);
        Assert.Equal(2, outdated.ServerVersion);
        Assert.Equal(LockState.None, outdated.LockState);

        await alice.GetLatestAsync(false, "Upright.SLDPRT");
        Assert.Equal("upright v2", Read(alice, "Upright.SLDPRT"));
        Assert.Equal(LocalState.UpToDate, (await StatusAsync(alice, "Upright.SLDPRT")).LocalState);
    }

    [Fact]
    public async Task CheckIn_WithoutCheckOut_IsRejected()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Hub.SLDPRT", "hub v1");
        await alice.CheckInPathsAsync("v1", "Hub.SLDPRT");

        Write(alice, "Hub.SLDPRT", "sneaky edit"); // cleared read-only by hand
        var ex = await Assert.ThrowsAsync<VaultException>(() => alice.CheckInPathsAsync("v2", "Hub.SLDPRT"));
        Assert.Contains("not checked out", ex.Message);
    }

    [Fact]
    public async Task ConcurrentCheckIns_OfDifferentFiles_BothLand()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        for (var i = 0; i < 3; i++)
        {
            Write(alice, $"Aero/Wing{i}.SLDPRT", $"wing {i}");
            Write(bob, $"Powertrain/Gear{i}.SLDPRT", $"gear {i}");
        }

        await Task.WhenAll(
            alice.CheckInAsync(Enumerable.Range(0, 3).Select(i => $"Aero/Wing{i}.SLDPRT").ToList(), null, new CheckInOptions { Comment = "wings" }),
            bob.CheckInAsync(Enumerable.Range(0, 3).Select(i => $"Powertrain/Gear{i}.SLDPRT").ToList(), null, new CheckInOptions { Comment = "gears" }));

        await alice.SyncAsync();
        Assert.Equal(6, alice.Head.Files.Count);
        await bob.SyncAsync();
        Assert.Equal(6, bob.Head.Files.Count);
    }

    [Fact]
    public async Task SameNewPath_AddedTwice_SecondIsRejected()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        Write(alice, "Brake/Rotor.SLDPRT", "alice rotor");
        Write(bob, "brake/rotor.sldprt", "bob rotor");

        await alice.CheckInPathsAsync("rotor", "Brake/Rotor.SLDPRT");
        await bob.SyncAsync();
        // Bob's file now collides (case-insensitively) with an existing vault file he never checked out.
        var ex = await Assert.ThrowsAsync<VaultException>(() => bob.CheckInPathsAsync("rotor", "brake/rotor.sldprt"));
        Assert.Equal(ErrorCodes.Conflict, ex.Code);
    }

    [Fact]
    public async Task UndoCheckOut_DiscardsChanges_AndReleasesLock()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Seat.SLDPRT", "seat v1");
        await alice.CheckInPathsAsync("v1", "Seat.SLDPRT");
        await alice.CheckOutAndApplyAsync("Seat.SLDPRT");
        Write(alice, "Seat.SLDPRT", "half-finished idea");

        var refused = await Assert.ThrowsAsync<VaultException>(() => alice.PrepareUndoCheckOutAsync(new[] { "Seat.SLDPRT" }, discardChanges: false));
        Assert.Equal(ErrorCodes.Conflict, refused.Code);

        var op = await alice.PrepareUndoCheckOutAsync(new[] { "Seat.SLDPRT" }, discardChanges: true);
        Assert.Single(op.Replacements);
        await alice.ApplyAsync(op);
        Assert.Equal("seat v1", Read(alice, "Seat.SLDPRT"));
        Assert.True(IsReadOnly(alice, "Seat.SLDPRT"));
        Assert.Empty(vault.Server.CurrentLocks);
        Assert.Equal(LocalState.UpToDate, (await StatusAsync(alice, "Seat.SLDPRT")).LocalState);
    }

    [Fact]
    public async Task KeepCheckedOut_LeavesFileWritableAndLocked()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Pedal.SLDPRT", "pedal v1");
        await alice.CheckInAsync(new[] { "Pedal.SLDPRT" }, null, new CheckInOptions { Comment = "v1", KeepCheckedOut = true });
        Assert.False(IsReadOnly(alice, "Pedal.SLDPRT"));
        Assert.Single(vault.Server.CurrentLocks);

        Write(alice, "Pedal.SLDPRT", "pedal v2");
        var result = await alice.CheckInPathsAsync("v2", "Pedal.SLDPRT");
        Assert.Contains("Pedal.SLDPRT v2", result.NewVersions);
        Assert.Empty(vault.Server.CurrentLocks);
    }

    [Fact]
    public async Task CheckInUnchangedFile_JustReleasesTheLock()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Bracket.SLDPRT", "bracket");
        await alice.CheckInPathsAsync("v1", "Bracket.SLDPRT");
        await alice.CheckOutAndApplyAsync("Bracket.SLDPRT");

        var result = await alice.CheckInPathsAsync("no change", "Bracket.SLDPRT");
        Assert.Null(result.Commit);
        Assert.Empty(result.NewVersions);
        Assert.Contains("Bracket.SLDPRT", result.Released);
        Assert.Equal(1, alice.Head.Get("Bracket.SLDPRT")!.Version);
        Assert.Empty(vault.Server.CurrentLocks);
    }

    [Fact]
    public async Task CheckedOutOnAnotherPc_IsReadOnlyHere_UntilTakenOver()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Nosecone.SLDPRT", "nose v1");
        await alice.CheckInPathsAsync("v1", "Nosecone.SLDPRT");
        await alice.CheckOutAndApplyAsync("Nosecone.SLDPRT");

        var laptop = await vault.OpenAsync("alice", "-laptop");
        await laptop.GetLatestAsync(false, "Nosecone.SLDPRT");
        Assert.Equal(LockState.MineElsewhere, (await StatusAsync(laptop, "Nosecone.SLDPRT")).LockState);
        Assert.True(IsReadOnly(laptop, "Nosecone.SLDPRT"));

        var ex = await Assert.ThrowsAsync<VaultException>(() => laptop.CheckOutAsync(new[] { "Nosecone.SLDPRT" }));
        Assert.Contains("another computer", ex.Message);

        var takeOver = await laptop.CheckOutAsync(new[] { "Nosecone.SLDPRT" }, takeOver: true);
        await laptop.ApplyAsync(takeOver);
        Assert.Equal(LockState.MineHere, (await StatusAsync(laptop, "Nosecone.SLDPRT")).LockState);
        Assert.False(IsReadOnly(laptop, "Nosecone.SLDPRT"));
    }

    [Fact]
    public async Task InterruptedCheckIn_IsFinishedOnRestart()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        Write(alice, "Firewall.SLDPRT", "firewall v1");
        await alice.CheckInAsync(new[] { "Firewall.SLDPRT" }, null, new CheckInOptions { Comment = "v1", KeepCheckedOut = true });

        // Simulate a crash after the push but before the unlock step.
        alice.State.PutJob(new Core.State.JobRecord("crash", "unlock", "pending", "[\"Firewall.SLDPRT\"]"));
        var restarted = await vault.ReopenAsync("alice");
        await restarted.ResumePendingAsync();

        Assert.Empty(vault.Server.CurrentLocks);
        Assert.True(IsReadOnly(restarted, "Firewall.SLDPRT"));
        Assert.Empty(restarted.State.GetJobs());
    }
}
