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
using Common.Network;
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
    public void StoppedLocalReadinessHeartbeatsPauseTheLocalHostLease()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var now = new System.DateTime(2026, 10, 18, 0, 0, 0, System.DateTimeKind.Utc);
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector(),
                () => now,
                System.TimeSpan.FromSeconds(3));
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");
            var network = new Mock<INetwork>();

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId,
                network.Object,
                identity);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            Assert.True(coordinator.IsFreshMapReady("host"));

            now = now.AddSeconds(4);
            handler.ReconcileCurrentAvailability();

            Assert.True(identity.CurrentLease.IsPaused);
            Assert.Equal(2, identity.Epoch);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => snapshot.IsPaused && snapshot.Epoch == 2)), Times.Once);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void LocalReadinessHeartbeatRenewsTheHostLeaseWhileMapReady()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var now = new System.DateTime(2026, 10, 18, 0, 0, 0, System.DateTimeKind.Utc);
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector(),
                () => now,
                System.TimeSpan.FromSeconds(3));
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId,
                Mock.Of<INetwork>(),
                identity);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            now = now.AddSeconds(2);
            handler.RefreshLocalAvailabilityAndReconcile();
            now = now.AddSeconds(2);
            handler.RefreshLocalAvailabilityAndReconcile();

            Assert.True(coordinator.IsFreshMapReady("host"));
            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal(1, identity.Epoch);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void ListenAuthorityCommitsAndReplicatesPausedLeaseThenResumesHostAtANewerEpoch()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var messageBroker = new TestMessageBroker();
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-a", "host", 1), "host"));
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(
                identity,
                new CampaignAuthorityCandidateSelector());
            var guestPeer = new TestNetwork().CreatePeer();
            var hostPlayer = new Player("host", "hero-host", "party-host", "clan-host", "character-host");
            var guestPlayer = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
            var playerManager = new Mock<IPlayerManager>();
            playerManager.Setup(manager => manager.TryGetPlayer("host", out hostPlayer)).Returns(true);
            playerManager.Setup(manager => manager.TryGetPlayer(guestPeer, out guestPlayer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(guestPeer)).Returns(true);
            var controllerId = new ControllerIdProvider();
            controllerId.SetControllerId("host");
            var network = new Mock<INetwork>();
            NetworkCampaignAuthorityHandoffBarrier appliedBarrier = default;
            network.Setup(value => value.Send(guestPeer, It.IsAny<NetworkCampaignAuthorityHandoffBarrier>()))
                .Callback<LiteNetLib.NetPeer, Common.Messaging.IMessage>((_, message) =>
                    appliedBarrier = (NetworkCampaignAuthorityHandoffBarrier)message);

            using var handler = new CampaignSimulationAvailabilityServerHandler(
                messageBroker,
                coordinator,
                connections.Object,
                playerManager.Object,
                controllerId,
                network.Object,
                identity);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            messageBroker.Publish(this, new CampaignReady());
            messageBroker.Publish(guestPeer, new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                1));
            Assert.Equal(1, identity.Epoch);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(false));

            Assert.True(identity.CurrentLease.IsPaused);
            Assert.Equal(2, identity.Epoch);
            Assert.Equal("guest", coordinator.Select().ControllerId);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => snapshot.IsPaused && snapshot.Epoch == 2)), Times.Once);
            network.Verify(value => value.Send(guestPeer, It.Is<NetworkCampaignAuthorityHandoffBarrier>(
                barrier => barrier.RoomId == identity.RoomId &&
                    barrier.PausedAuthorityId == identity.AuthorityId &&
                    barrier.PausedEpoch == 2 &&
                    barrier.CandidateControllerId == "guest" &&
                    barrier.BarrierSequence > 0)), Times.Once);
            Assert.True(appliedBarrier.BarrierSequence > 0);

            var applied = new NetworkCampaignAuthorityHandoffBarrierApplied(
                appliedBarrier.RoomId, appliedBarrier.PausedAuthorityId, appliedBarrier.PausedEpoch,
                appliedBarrier.CandidateControllerId, appliedBarrier.BarrierSequence);
            messageBroker.Publish(guestPeer, applied);

            // The ordered barrier is a prerequisite, not authority activation: clients still lack
            // the server-side world mutation relay needed to publish their simulation results.
            Assert.True(identity.CurrentLease.IsPaused);
            Assert.Equal(2, identity.Epoch);

            messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));

            Assert.False(identity.CurrentLease.IsPaused);
            Assert.Equal("host", identity.AuthorityControllerId);
            Assert.Equal(3, identity.Epoch);
            network.Verify(value => value.SendAll(It.Is<NetworkAuthorityLeaseSnapshot>(
                snapshot => !snapshot.IsPaused && snapshot.AuthorityControllerId == "host" && snapshot.Epoch == 3)), Times.Once);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

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
