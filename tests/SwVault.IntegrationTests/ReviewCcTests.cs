using SwVault.Agent;
using SwVault.Core.Client;

namespace SwVault.IntegrationTests;

/// <summary>Who gets copied on a review request: the subsystem's approved REs, when a general member asks.</summary>
public class ReviewCcTests
{
    private static readonly Car[] Cars =
    {
        new("c-1", "2027 Car", "FS27", new[]
        {
            new Subsystem("s-1", "Suspension", "FS27/Suspension", "c-1", "2027 Car", new[]
            {
                new SubsystemEngineer("bob", true),
                new SubsystemEngineer("carol", true),
                new SubsystemEngineer("eli", false), // not approved yet
                new SubsystemEngineer("dana", true), // also the lead
            }),
        }),
    };

    private static readonly TeamDirectory Directory = new(new[]
    {
        new TeamPerson("dana", "Dana", new TeamProfile(true, "Suspension")),
        new TeamPerson("frank", "Frank", new TeamProfile(false, "Suspension")),
        new TeamPerson("bob", "Bob", new TeamProfile(false, "Suspension")),
    }, new[] { "Suspension" });

    [Fact]
    public void MemberRequest_CopiesApprovedEngineersExceptLeadAndRequester()
    {
        var (cc, reason) = AgentHost.ReviewCc(Directory, Cars, "frank", "dana", "FS27/Suspension/Rocker.SLDPRT");
        Assert.Equal(new[] { "bob", "carol" }, cc);
        Assert.Equal("responsible engineers of 2027 Car / Suspension", reason);

        (cc, _) = AgentHost.ReviewCc(Directory, Cars, "bob", "dana", "FS27/Suspension/Rocker.SLDPRT");
        Assert.Equal(new[] { "carol" }, cc);
    }

    [Fact]
    public void NoCc_ForLeadsOrFilesOutsideSubsystems()
    {
        Assert.Empty(AgentHost.ReviewCc(Directory, Cars, "dana", "swadmin", "FS27/Suspension/Rocker.SLDPRT").Cc);
        var (cc, reason) = AgentHost.ReviewCc(Directory, Cars, "frank", "dana", "FS27/Chassis/Frame.SLDPRT");
        Assert.Empty(cc);
        Assert.Null(reason);
    }
}
