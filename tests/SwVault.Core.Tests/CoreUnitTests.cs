using System.Text;
using SwVault.Core.Client;
using SwVault.Core.Index;
using SwVault.Core.Util;
using SwVault.Core.Vault;
using SwVault.Protocol;

namespace SwVault.Core.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData(@"Chassis\Frame.SLDPRT", "Chassis/Frame.SLDPRT")]
    [InlineData("/Chassis//Frame.SLDPRT/", "Chassis/Frame.SLDPRT")]
    [InlineData("Frame.SLDPRT", "Frame.SLDPRT")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, PathRules.Normalize(input));

    [Fact]
    public void ToVaultPath_MapsInsideRootOnly()
    {
        Assert.Equal("Chassis/Frame.SLDPRT", PathRules.ToVaultPath(@"C:\SWVault\FSAE", @"C:\SWVault\FSAE\Chassis\Frame.SLDPRT"));
        Assert.Equal("Chassis/Frame.SLDPRT", PathRules.ToVaultPath(@"C:\SWVault\FSAE\", @"c:\swvault\fsae\Chassis\Frame.SLDPRT"));
        Assert.Null(PathRules.ToVaultPath(@"C:\SWVault\FSAE", @"C:\SWVault\FSAE2\Frame.SLDPRT"));
        Assert.Null(PathRules.ToVaultPath(@"C:\SWVault\FSAE", @"C:\SOLIDWORKS Data\browser\bolt.sldprt"));
    }

    [Fact]
    public void MetaPaths_RoundTrip()
    {
        var meta = PathRules.MetaPathFor("Chassis/Frame.SLDPRT");
        Assert.Equal(".swvault/meta/Chassis/Frame.SLDPRT.json", meta);
        Assert.Equal("Chassis/Frame.SLDPRT", PathRules.FileForMetaPath(meta));
        Assert.Null(PathRules.FileForMetaPath("Chassis/Frame.SLDPRT"));
    }

    [Theory]
    [InlineData(".gitattributes", true)]
    [InlineData(".swvault/vault.json", true)]
    [InlineData(".swvault-local/tmp/x.part", true)]
    [InlineData("Chassis/.swvault-notes.txt", false)]
    [InlineData("Frame.SLDPRT", false)]
    public void IsInternal(string path, bool expected) => Assert.Equal(expected, PathRules.IsInternal(path));

    [Fact]
    public void SyncedFolderDetection()
    {
        Assert.True(PathRules.IsUnderSyncedFolder(@"C:\Users\someone\OneDrive\Documents\Vault"));
        Assert.True(PathRules.IsUnderSyncedFolder(@"C:\Users\someone\Dropbox\CAD"));
        Assert.False(PathRules.IsUnderSyncedFolder(@"C:\SWVault\FSAE"));
    }
}

public class IgnoreRulesTests
{
    private static readonly IgnoreRules Rules = new(VaultConfig.DefaultIgnore());

    [Theory]
    [InlineData("Chassis/~$Frame.SLDPRT", true)]  // SOLIDWORKS lock files
    [InlineData("Frame.swbak", true)]
    [InlineData("Chassis/Thumbs.db", true)]
    [InlineData(".swvault-local/tmp/1.part", true)]
    [InlineData("Chassis/Frame.SLDPRT", false)]
    [InlineData("Drawings/Frame.SLDDRW", false)]
    public void DefaultIgnoreList(string path, bool ignored) => Assert.Equal(ignored, Rules.IsIgnored(path));

    [Fact]
    public void FullPathAndFolderPatterns()
    {
        var rules = new IgnoreRules(new[] { "Scratch/", "Renders/*.png" });
        Assert.True(rules.IsIgnored("Scratch/idea.SLDPRT"));
        Assert.True(rules.IsIgnored("Chassis/Scratch/idea.SLDPRT"));
        Assert.True(rules.IsIgnored("Renders/car.png"));
        Assert.False(rules.IsIgnored("Chassis/Renders/car.png"));
        Assert.False(rules.IsIgnored("Scratch.SLDPRT"));
    }
}

public class LfsPointerTests
{
    private const string Oid = "4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393";

    [Fact]
    public void FormatsLikeGitLfs()
    {
        var text = new LfsPointer(Oid, 12345).ToText();
        Assert.Equal($"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 12345\n", text);
    }

    [Fact]
    public void ParsesRoundTrip()
    {
        Assert.True(LfsPointer.TryParse(new LfsPointer(Oid, 42).ToBytes(), out var parsed));
        Assert.Equal(Oid, parsed.Oid);
        Assert.Equal(42, parsed.Size);
    }

    [Fact]
    public void RejectsNonPointers()
    {
        Assert.False(LfsPointer.TryParse(Encoding.UTF8.GetBytes("just some text that is long enough to be checked"), out _));
        Assert.False(LfsPointer.TryParse(Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 1\n"), out _));
    }
}

public class WorkflowTests
{
    private static readonly WorkflowConfig Alpha = WorkflowConfig.CreateDefault();

    [Theory]
    [InlineData(null, "A")]
    [InlineData("", "A")]
    [InlineData("A", "B")]
    [InlineData("H", "J")]   // I is skipped
    [InlineData("N", "P")]   // O is skipped
    [InlineData("Y", "AA")]  // Z is skipped, then two letters
    [InlineData("AY", "BA")]
    public void AlphaRevisions(string? current, string expected) => Assert.Equal(expected, Revisions.Next(current, Alpha));

    [Fact]
    public void NumericRevisions()
    {
        var numeric = new WorkflowConfig { RevisionScheme = "numeric" };
        Assert.Equal("1", Revisions.Next(null, numeric));
        Assert.Equal("10", Revisions.Next("9", numeric));
    }

    private static HeadFile File(string path, string? state, params string[] references) => new()
    {
        Path = path,
        BlobSha = "x",
        Oid = "y",
        Meta = new FileMeta
        {
            Version = 1,
            State = state,
            References = references.Select(r => new ReferenceEntry { Path = r, Version = 1 }).ToList(),
        },
    };

    [Fact]
    public void RolesAndStatesAreEnforced()
    {
        var config = VaultConfig.CreateDefault("T", @"C:\SWVault\T", "lead", null);
        var file = File("Asm.SLDASM", "InReview");
        var head = TestHead(file);

        Assert.False(WorkflowEngine.Evaluate(config, file, "Approve", "designer", head).Allowed);
        var approved = WorkflowEngine.Evaluate(config, file, "Approve", "lead", head);
        Assert.True(approved.Allowed);
        Assert.Equal("A", approved.NextRevision);

        var fromWrongState = WorkflowEngine.Evaluate(config, File("P.SLDPRT", "WIP"), "Approve", "lead", head);
        Assert.False(fromWrongState.Allowed);
        Assert.Contains("not available", fromWrongState.Reason);
    }

    [Fact]
    public void UnreleasedReferencesWarnByDefault_AndCanBlock()
    {
        var config = VaultConfig.CreateDefault("T", @"C:\SWVault\T", "lead", null);
        var part = File("Part.SLDPRT", "WIP");
        var asm = File("Asm.SLDASM", "InReview", "Part.SLDPRT");
        var head = TestHead(part, asm);

        var warn = WorkflowEngine.Evaluate(config, asm, "Approve", "lead", head);
        Assert.True(warn.Allowed);
        Assert.Single(warn.Warnings);

        config.Workflow.Transitions.Single(t => t.Name == "Approve").RequireReferencesReleased = "block";
        var block = WorkflowEngine.Evaluate(config, asm, "Approve", "lead", head);
        Assert.False(block.Allowed);
        Assert.Contains("Part.SLDPRT", block.Reason);
    }

    [Fact]
    public void RoleMatchingIsCaseInsensitive()
    {
        var config = VaultConfig.CreateDefault("T", @"C:\SWVault\T", "Wes-Miller", null);
        Assert.True(config.UserHasRole("wes-miller", "ADMIN"));
        Assert.True(config.UserHasRole("anyone", "*"));
        Assert.False(config.UserHasRole(null, "*"));
    }

    private static HeadIndex TestHead(params HeadFile[] files) =>
        HeadIndex.FromFiles(files, VaultConfig.CreateDefault("T", @"C:\SWVault\T", "lead", null));
}

public class WireJsonTests
{
    [Fact]
    public void ProtocolRoundTrip()
    {
        var request = new JobRequest
        {
            Kind = JobKind.CheckIn,
            Paths = new[] { @"C:\SWVault\FSAE\Frame.SLDPRT" },
            Comment = "Lighter",
            Files = new[] { new CheckInFileInfo { LocalPath = "x", Properties = new() { ["PartNo"] = "100" } } },
        };
        var json = WireJson.Serialize(request);
        Assert.Contains("\"PartNo\":\"100\"", json);
        var back = WireJson.Deserialize<JobRequest>(json);
        Assert.Equal(JobKind.CheckIn, back.Kind);
        Assert.Equal("100", back.Files[0].Properties["PartNo"]);

        var envelope = new RpcMessage { Id = 7, Method = Methods.JobStart, Payload = json };
        var parsed = WireJson.Deserialize<RpcMessage>(WireJson.Serialize(envelope));
        Assert.True(parsed.IsRequest);
        Assert.Equal(json, parsed.Payload);
    }
}

public class TeamConfigTests
{
    /// <summary>Exactly what server/linux/lib.sh (write_team_json) produces.</summary>
    [Fact]
    public void ReadsTeamJsonFromServerSetup()
    {
        const string json = """
            {
              "name": "FSAE",
              "vaultUrl": "https://swvault.tail1234.ts.net/fsae/cad.git",
              "localRoot": "C:\\SWVault\\FSAE",
              "admin": "wes",
              "downloadAllOnJoin": true,
              "solidworksVersion": "2025"
            }
            """;
        var team = Json.Deserialize<TeamConfig>(json)!;
        Assert.Equal("FSAE", team.Name);
        Assert.Equal("https://swvault.tail1234.ts.net/fsae/cad.git", team.VaultUrl);
        Assert.Equal(@"C:\SWVault\FSAE", team.LocalRoot);
        Assert.Equal("wes", team.Admin);
        Assert.Equal("2025", team.SolidworksVersion);
        Assert.True(team.DownloadAllOnJoin);
    }
}

public class TeamInviteTests
{
    [Theory]
    [InlineData("K7QM-R3XT-9BWE", "K7QM-R3XT-9BWE")]
    [InlineData("k7qm r3xt 9bwe", "K7QM-R3XT-9BWE")]
    [InlineData("k7qmr3xt9bwe", "K7QM-R3XT-9BWE")]
    [InlineData(@"C:\Users\a\Downloads\SwVault-FSAE-invite-K7QM-R3XT-9BWE", "K7QM-R3XT-9BWE")]
    [InlineData("K7QM-R3XT", null)]
    [InlineData("O0I1-R3XT-9BWE", null)] // look-alike characters are never in codes
    [InlineData("", null)]
    public void NormalizeCode(string input, string? expected) => Assert.Equal(expected, TeamInvites.NormalizeCode(input));

    [Fact]
    public void ServiceUrlSitsNextToTheServer()
    {
        Assert.Equal("https://swvault.tail1234.ts.net/swvault-invites/", TeamInvites.ServiceUrl("https://swvault.tail1234.ts.net/fsae/cad.git").ToString());
        Assert.Equal("https://swvault.tail1234.ts.net/swvault-invites/download?invite=K7QM-R3XT-9BWE",
            TeamInvites.DownloadUrl("https://swvault.tail1234.ts.net/fsae/cad.git", "K7QM-R3XT-9BWE").ToString());
    }
}

public class ReviewParsingTests
{
    private static System.Text.Json.JsonElement Issue(string body, string state, params string[] labels) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            number = 7,
            body,
            state,
            html_url = "https://x/fsae/cad/issues/7",
            created_at = "2026-09-30T10:00:00Z",
            updated_at = "2026-09-30T11:00:00Z",
            user = new { login = "eli" },
            assignees = new[] { new { login = "dana" } },
            labels = labels.Select(l => new { name = l }).ToArray(),
        })).RootElement;

    private const string Body = "<!-- swvault-review {\"kind\":\"simulation\",\"path\":\"Chassis/Frame.SLDPRT\",\"version\":3} -->\n**Simulation review** requested by @eli\n\n> check FOS\n> at the tabs\n";

    [Fact]
    public void ReadsTheRequest()
    {
        var r = Reviews.Parse(Issue(Body, "open", "review", "review: simulation"))!;
        Assert.Equal(ReviewKind.Simulation, r.Kind);
        Assert.Equal("Chassis/Frame.SLDPRT", r.Path);
        Assert.Equal(3, r.Version);
        Assert.Equal("eli", r.Requester);
        Assert.Equal("dana", r.Lead);
        Assert.Equal("check FOS\nat the tabs", r.Message);
        Assert.Equal(ReviewStatus.Waiting, r.Status);
    }

    [Theory]
    [InlineData("open", "review: changes requested", ReviewStatus.ChangesRequested)]
    [InlineData("closed", "review: approved", ReviewStatus.Approved)]
    [InlineData("closed", "review: cancelled", ReviewStatus.Cancelled)]
    [InlineData("closed", "review", ReviewStatus.Cancelled)] // closed on the web without a decision
    public void StatusComesFromLabels(string state, string label, ReviewStatus expected) =>
        Assert.Equal(expected, Reviews.Parse(Issue(Body, state, "review", label))!.Status);

    [Fact]
    public void IgnoresOtherIssues() => Assert.Null(Reviews.Parse(Issue("Just a normal issue", "open")));
}
