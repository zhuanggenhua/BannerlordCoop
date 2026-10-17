using Common;
using Common.Messaging;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Time.Messages;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignSimulationAvailabilityServerHandlerTests
{
    [Fact]
    public void ListenAuthorityPrefersHostThenGuestAndPausesAfterGuestDisconnects()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TrySetRoomHostControllerId("host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector());
            var peer = new TestNetwork().CreatePeer();
            var localPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var remotePlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out localPlayer)).Returns(true);
            playerManager.Setup(manager => manager.TryGetPlayer(peer, out remotePlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(true);
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId);

            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(peer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));

            Assert.Equal("host", coordinator.Select().ControllerId);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            Assert.Equal("guest", coordinator.Select().ControllerId);

            messageBroker.Publish(this, new PlayerDisconnected(peer, default));
            Assert.True(coordinator.Select().IsPaused);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void IgnoresAvailabilityReportsUntilCampaignSynchronizationCompletes()
    {
        var messageBroker = new TestMessageBroker();
        var coordinator = new CampaignAuthorityAvailabilityCoordinator(
            new SessionIdentity(),
            new CampaignAuthorityCandidateSelector());
        var peer = new TestNetwork().CreatePeer();
        var remotePlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(peer, out remotePlayer)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(false);
        var controllerId = new ControllerIdProvider();
        using var handler = new CampaignSimulationAvailabilityServerHandler(
            messageBroker,
            coordinator,
            connections.Object,
            playerManager.Object,
            controllerId);

        messageBroker.Publish(peer, new NetworkCampaignSimulationAvailabilityReport(
            CampaignSimulationAvailability.MapReady,
            1));

        Assert.True(coordinator.Select().IsPaused);
    }
}
