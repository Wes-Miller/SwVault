using SwVault.Core;
using SwVault.Protocol;
using static SwVault.IntegrationTests.TestVault;

namespace SwVault.IntegrationTests;

public class AssemblyHistoryWorkflowTests
{
    private static async Task<(VaultSession Alice, VaultSession Bob)> SeedAssemblyAsync(TestVault vault)
    {
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        var part1 = Write(alice, "Suspension/ArmUpper.SLDPRT", "upper arm v1");
        var part2 = Write(alice, "Suspension/ArmLower.SLDPRT", "lower arm v1");
        var asm = Write(alice, "Suspension/FrontCorner.SLDASM", "corner asm v1");
        var infos = new[]
        {
            new CheckInFileInfo { LocalPath = asm, References = new[] { part1, part2 }, Properties = new() { ["PartNo"] = "SUS-100", ["Description"] = "Front corner" }, SwVersion = "2025 SP3" },
            new CheckInFileInfo { LocalPath = part1, Properties = new() { ["PartNo"] = "SUS-101" }, SwVersion = "2025 SP3" },
            new CheckInFileInfo { LocalPath = part2, Properties = new() { ["PartNo"] = "SUS-102" }, SwVersion = "2025 SP3" },
        };
        await alice.CheckInAsync(new[] { asm, part1, part2 }, infos, new CheckInOptions { Comment = "Front corner" });
        return (alice, bob);
    }

    [Fact]
    public async Task GetLatestWithReferences_PullsTheWholeAssembly()
    {
        await using var vault = await TestVault.CreateAsync();
        var (alice, bob) = await SeedAssemblyAsync(vault);

        var head = alice.Head.Get("Suspension/FrontCorner.SLDASM")!;
        Assert.Equal(2, head.Meta!.References!.Count);
        Assert.Equal("SUS-100", head.Meta.Properties!["PartNo"]);
        Assert.Contains("Suspension/FrontCorner.SLDASM", alice.WhereUsed("Suspension/ArmUpper.SLDPRT"));

        var result = await bob.GetLatestAsync(true, "Suspension/FrontCorner.SLDASM");
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal("lower arm v1", Read(bob, "Suspension/ArmLower.SLDPRT"));

        var tree = await bob.GetReferenceTreeAsync("Suspension/FrontCorner.SLDASM", recursive: true);
        Assert.Equal(2, tree!.Children!.Length);
        Assert.All(tree.Children, c => Assert.Equal(LocalState.UpToDate, c.Status!.LocalState));

        var search = await bob.SearchAsync("SUS-10", StatusFilter.All, 50);
        Assert.Equal(3, search.Count);
    }

    [Fact]
    public async Task MissingReference_BlocksCheckIn_UntilIncluded()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var part = Write(alice, "Brakes/Caliper.SLDPRT", "caliper");
        var asm = Write(alice, "Brakes/Corner.SLDASM", "asm");
        var infos = new[] { new CheckInFileInfo { LocalPath = asm, References = new[] { part } } };

        var ex = await Assert.ThrowsAsync<VaultException>(() => alice.CheckInAsync(new[] { asm }, infos, new CheckInOptions()));
        Assert.Contains("Caliper", ex.Message);

        await alice.CheckInAsync(new[] { asm, part }, infos, new CheckInOptions { Comment = "both" });
        Assert.NotNull(alice.Head.Get("Brakes/Caliper.SLDPRT"));
    }

    [Fact]
    public async Task History_GetVersionAsBuilt_AndRollback()
    {
        await using var vault = await TestVault.CreateAsync();
        var (alice, bob) = await SeedAssemblyAsync(vault);

        // Part moves on to v2 without the assembly changing.
        await bob.CheckOutAndApplyAsync("Suspension/ArmUpper.SLDPRT");
        Write(bob, "Suspension/ArmUpper.SLDPRT", "upper arm v2");
        await bob.CheckInPathsAsync("Thicker wall", "Suspension/ArmUpper.SLDPRT");

        var history = await alice.GetHistoryAsync("Suspension/ArmUpper.SLDPRT");
        Assert.Equal(new[] { 2, 1 }, history.Select(h => h.Version).ToArray());
        Assert.Equal("Thicker wall", history[0].Comment);
        Assert.Equal("bob", history[0].By);

        // "As built": the assembly's v1 used upper arm v1.
        await alice.GetLatestAsync(true, "Suspension/FrontCorner.SLDASM");
        Assert.Equal("upper arm v2", Read(alice, "Suspension/ArmUpper.SLDPRT"));
        var asBuilt = await alice.PrepareGetVersionAsync("Suspension/FrontCorner.SLDASM", 1, asBuilt: true);
        await alice.ApplyAsync(asBuilt);
        Assert.Equal("upper arm v1", Read(alice, "Suspension/ArmUpper.SLDPRT"));
        var status = await StatusAsync(alice, "Suspension/ArmUpper.SLDPRT");
        Assert.Equal(LocalState.Outdated, status.LocalState);
        Assert.Equal(1, status.LocalVersion);

        // Rollback: v1 content comes back as a new version 3.
        await alice.CheckOutAndApplyAsync("Suspension/ArmUpper.SLDPRT");
        Assert.Equal("upper arm v2", Read(alice, "Suspension/ArmUpper.SLDPRT"));
        var rollback = await alice.PrepareRollbackAsync("Suspension/ArmUpper.SLDPRT", 1);
        await alice.ApplyAsync(rollback);
        Assert.Equal(LocalState.Modified, (await StatusAsync(alice, "Suspension/ArmUpper.SLDPRT")).LocalState);
        var result = await alice.CheckInPathsAsync("Back to v1 geometry", "Suspension/ArmUpper.SLDPRT");
        Assert.Contains("Suspension/ArmUpper.SLDPRT v3", result.NewVersions);
        await bob.GetLatestAsync(false, "Suspension/ArmUpper.SLDPRT");
        Assert.Equal("upper arm v1", Read(bob, "Suspension/ArmUpper.SLDPRT"));
    }

    [Fact]
    public async Task ReleaseWorkflow_EnforcesRolesStatesAndRevisions()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice"); // admin + approver
        var bob = await vault.OpenAsync("bob");     // designer
        Write(bob, "Frame/Tube.SLDPRT", "tube v1");
        await bob.CheckInPathsAsync("v1", "Frame/Tube.SLDPRT");

        var options = await bob.GetTransitionsAsync("Frame/Tube.SLDPRT");
        Assert.Contains(options, o => o.Name == "Submit for review" && o.Allowed);
        await bob.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Submit for review", "Ready for review");
        Assert.Equal("InReview", (await StatusAsync(bob, "Frame/Tube.SLDPRT")).State);

        // In review: nobody can check it out, and only approvers can approve.
        var blocked = await Assert.ThrowsAsync<VaultException>(() => bob.CheckOutAsync(new[] { "Frame/Tube.SLDPRT" }));
        Assert.Contains("InReview", blocked.Message);
        var denied = await Assert.ThrowsAsync<VaultException>(() => bob.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Approve", null));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);

        await alice.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Approve", "Released for manufacturing");
        var released = await StatusAsync(alice, "Frame/Tube.SLDPRT");
        Assert.Equal("Released", released.State);
        Assert.Equal("A", released.Revision);

        // Change request -> edit -> release again bumps to B. Content version keeps counting separately.
        await bob.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Change request", "Needs a gusset");
        await bob.CheckOutAndApplyAsync("Frame/Tube.SLDPRT");
        Write(bob, "Frame/Tube.SLDPRT", "tube v2");
        await bob.CheckInPathsAsync("Added gusset", "Frame/Tube.SLDPRT");
        await bob.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Submit for review", null);

        // The approver stamps the revision into the file and releases in one check-in.
        await alice.GetLatestAsync(false, "Frame/Tube.SLDPRT");
        var check = (await alice.GetTransitionsAsync("Frame/Tube.SLDPRT")).Single(o => o.Name == "Approve");
        Assert.Equal("B", check.NextRevision);
        var meta = alice.Head.Get("Frame/Tube.SLDPRT")!.Meta!;
        Assert.Equal("A", meta.Revision);
        Assert.Single(meta.RevisionHistory!);

        await alice.TransitionAsync(new[] { "Frame/Tube.SLDPRT" }, "Approve", null);
        var final = alice.Head.Get("Frame/Tube.SLDPRT")!.Meta!;
        Assert.Equal("Released", final.State);
        Assert.Equal("B", final.Revision);
        Assert.Equal(2, final.Version);
        Assert.Equal(new[] { "A", "B" }, final.RevisionHistory!.Select(r => r.Rev).ToArray());
        Assert.Empty(vault.Server.CurrentLocks);
    }

    [Fact]
    public async Task ReleaseCheckIn_StampsRevisionInSameCommitAsContent()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice"); // approver
        var bob = await vault.OpenAsync("bob");
        Write(bob, "Drawings/Tube.SLDDRW", "drawing v1");
        await bob.CheckInPathsAsync("v1", "Drawings/Tube.SLDDRW");
        await bob.TransitionAsync(new[] { "Drawings/Tube.SLDDRW" }, "Submit for review", null);

        // A plain check-out of an InReview file is refused, and a designer can't check out "for Approve".
        await Assert.ThrowsAsync<VaultException>(() => alice.CheckOutAsync(new[] { "Drawings/Tube.SLDDRW" }));
        await Assert.ThrowsAsync<VaultException>(() => bob.CheckOutAsync(new[] { "Drawings/Tube.SLDDRW" }, forTransition: "Approve"));

        // The approver checks out for the release, the add-in stamps "Rev A" into the file, and the
        // content change + state + revision land in one commit.
        var op = await alice.CheckOutAsync(new[] { "Drawings/Tube.SLDDRW" }, forTransition: "Approve");
        await alice.ApplyAsync(op);
        Write(alice, "Drawings/Tube.SLDDRW", "drawing v1 + title block Rev A");
        var pdf = Write(alice, "Drawings/_Released/Tube_RevA.pdf", "pdf bytes");
        var result = await alice.CheckInAsync(new[] { "Drawings/Tube.SLDDRW", pdf }, null,
            new CheckInOptions { Comment = "Released", TransitionName = "Approve" });

        Assert.NotNull(result.Commit);
        var meta = alice.Head.Get("Drawings/Tube.SLDDRW")!.Meta!;
        Assert.Equal(2, meta.Version);
        Assert.Equal("Released", meta.State);
        Assert.Equal("A", meta.Revision);
        Assert.Equal(2, meta.RevisionHistory![0].Version);
        Assert.Equal("Released", alice.Head.Get("Drawings/_Released/Tube_RevA.pdf")!.Meta!.State);
        Assert.Empty(vault.Server.CurrentLocks);
    }

    [Fact]
    public async Task Delete_BlockedWhileUsed_AdminCanForce()
    {
        await using var vault = await TestVault.CreateAsync();
        var (alice, bob) = await SeedAssemblyAsync(vault);

        var blocked = await Assert.ThrowsAsync<VaultException>(() => bob.DeleteAsync(new[] { "Suspension/ArmLower.SLDPRT" }, null, force: false));
        Assert.Contains("FrontCorner", blocked.Message);
        var notAdmin = await Assert.ThrowsAsync<VaultException>(() => bob.DeleteAsync(new[] { "Suspension/ArmLower.SLDPRT" }, null, force: true));
        Assert.Equal(ErrorCodes.Forbidden, notAdmin.Code);

        await alice.DeleteAsync(new[] { "Suspension/ArmLower.SLDPRT" }, "Obsolete part", force: true);
        Assert.Null(alice.Head.Get("Suspension/ArmLower.SLDPRT"));
        Assert.False(File.Exists(alice.LocalPathOf("Suspension/ArmLower.SLDPRT")));
        Assert.Empty(vault.Server.CurrentLocks);

        // Deleting the whole folder together with its parent assembly is allowed.
        await bob.DeleteAsync(new[] { "Suspension" }, "Redesign", force: false);
        await alice.SyncAsync();
        Assert.Empty(alice.Head.Files);
    }

    [Fact]
    public async Task ForceUnlock_ByAdminOnly()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var bob = await vault.OpenAsync("bob");
        var carol = await vault.OpenAsync("carol");
        Write(alice, "Tank.SLDPRT", "tank");
        await alice.CheckInPathsAsync("v1", "Tank.SLDPRT");
        await carol.CheckOutAndApplyAsync("Tank.SLDPRT");

        var denied = await Assert.ThrowsAsync<VaultException>(() => bob.ForceUnlockAsync("Tank.SLDPRT"));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);
        await alice.ForceUnlockAsync("Tank.SLDPRT");
        Assert.Empty(vault.Server.CurrentLocks);

        // Carol's check-in now fails loudly instead of silently overwriting.
        Write(carol, "Tank.SLDPRT", "carol's edit");
        var lost = await Assert.ThrowsAsync<VaultException>(() => carol.CheckInPathsAsync("mine", "Tank.SLDPRT"));
        Assert.Equal(ErrorCodes.Conflict, lost.Code);
    }

    [Fact]
    public async Task Import_CommitsNewFiles_AndIsResumable()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var paths = Enumerable.Range(0, 12).Select(i => Write(alice, $"Imported/Part{i:00}.SLDPRT", $"part {i}")).ToList();

        var first = await alice.CheckInAsync(paths.Take(5).ToList(), null, new CheckInOptions { IsImport = true, Comment = "Import from share" });
        Assert.Equal(5, first.NewVersions.Count);

        var rest = await alice.CheckInAsync(paths, null, new CheckInOptions { IsImport = true, Comment = "Import from share" });
        Assert.Equal(7, rest.NewVersions.Count); // the first five were skipped as already imported
        Assert.Equal(12, alice.Head.Files.Count);
        Assert.All(paths, p => Assert.True(File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)));
        Assert.Empty(vault.Server.CurrentLocks);
    }

    [Fact]
    public async Task SolidworksVersionGuard_BlocksNewerVersion()
    {
        await using var vault = await TestVault.CreateAsync();
        var alice = await vault.OpenAsync("alice");
        var path = Write(alice, "Future.SLDPRT", "saved in 2026");
        var infos = new[] { new CheckInFileInfo { LocalPath = path, SwVersion = "2026 SP0" } };
        var ex = await Assert.ThrowsAsync<VaultException>(() => alice.CheckInAsync(new[] { path }, infos, new CheckInOptions()));
        Assert.Contains("SOLIDWORKS 2025", ex.Message);
    }
}
