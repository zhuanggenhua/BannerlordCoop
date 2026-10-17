using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.Time.Patches;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Time.Patches;

public class CampaignTimePacingPatchTests
{
    [Fact]
    public void AuthorityReplicaDoesNotApplyClientTimeExtrapolation()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
                return true;
            });

        Assert.False(CampaignTimePacingPatches.ShouldApplyClientSimulationTime(true, true, authority.Object));
    }

    [Fact]
    public void NonAuthorityReplicaAppliesClientTimeExtrapolation()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
                return false;
            });

        Assert.True(CampaignTimePacingPatches.ShouldApplyClientSimulationTime(true, true, authority.Object));
    }

    [Fact]
    public void PausedLeaseDoesNotExtrapolateCampaignTime()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = new AuthorityLease("room-a", "authority-paused", string.Empty, 2, isPaused: true);
                return false;
            });

        Assert.False(CampaignTimePacingPatches.ShouldApplyClientSimulationTime(true, true, authority.Object));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnknownLeaseFallsBackToProcessMode(bool isServer, bool shouldApplyClientTime)
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = default;
                return false;
            });

        Assert.Equal(
            shouldApplyClientTime,
            CampaignTimePacingPatches.ShouldApplyClientSimulationTime(isServer, true, authority.Object));
    }
}
