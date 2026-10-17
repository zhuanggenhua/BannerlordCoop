using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Time.Patches;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Time.Patches;

public class MapStatePatchTests
{
    [Fact]
    public void ListenAuthorityTicksMapWhenItHoldsTheLease()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
                return true;
            });

        Assert.True(MapStatePatch.ShouldTickMapMode(true, true, authority.Object));
    }

    [Fact]
    public void ListenAuthoritySkipsMapTickAfterLeaseMovesAway()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
                return false;
            });

        Assert.False(MapStatePatch.ShouldTickMapMode(true, true, authority.Object));
    }

    [Fact]
    public void OtherModesKeepTheirExistingMapTickPath()
    {
        Assert.True(MapStatePatch.ShouldTickMapMode(false, false, null));
        Assert.True(MapStatePatch.ShouldTickMapMode(true, false, null));
    }

    [Fact]
    public void ListenAuthorityKeepsLegacyPathUntilItsLeaseIsConfirmed()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = default;
                return false;
            });

        Assert.True(MapStatePatch.ShouldTickMapMode(true, true, authority.Object));
    }
}
