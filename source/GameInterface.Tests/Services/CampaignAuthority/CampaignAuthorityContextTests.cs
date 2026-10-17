using GameInterface.Services.CampaignAuthority;
using Common;
using GameInterface.Services.Entity;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignAuthorityContextTests
{
    [Fact]
    public void LocalAuthorityUsesLeaseControllerAndNotRoomHostIdentity()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
            Assert.True(identity.TryApply(lease, "controller-a"));
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var context = new CampaignAuthorityContext(identity, controller);

            Assert.True(context.IsAuthorityKnown);
            Assert.False(context.IsLocalCampaignAuthority);
            Assert.True(context.TryGetCurrentLease(out var currentLease));
            Assert.Equal(lease, currentLease);

            controller.SetControllerId("controller-b");
            Assert.True(context.IsLocalCampaignAuthority);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void UnconfirmedLeaseDoesNotGrantLocalCampaignAuthority()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var context = new CampaignAuthorityContext(new SessionIdentity(), controller);

            Assert.False(context.IsAuthorityKnown);
            Assert.False(context.IsLocalCampaignAuthority);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void PausedLeaseGrantsNoCampaignAuthorityEvenToDedicatedProcess()
    {
        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.DedicatedAuthority;
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(
                new AuthorityLease("room-a", "authority-paused", string.Empty, 2, isPaused: true)));
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var context = new CampaignAuthorityContext(identity, controller);

            Assert.True(context.IsAuthorityKnown);
            Assert.False(context.IsLocalCampaignAuthority);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }
}
