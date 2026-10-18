using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignAuthorityHandoffFaultTests
{
    [Fact]
    public void WithdrawnCandidateCannotUseDelayedOrForgedBarrierAcknowledgements()
    {
        using var scenario = new HandoffScenario(paused: true);
        var first = scenario.LastSent<NetworkCampaignAuthorityHandoffBarrier>();

        scenario.ReportGuest(CampaignSimulationAvailability.Unavailable, 2);
        scenario.ReportGuest(CampaignSimulationAvailability.MapReady, 3);
        var replacement = scenario.LastSent<NetworkCampaignAuthorityHandoffBarrier>();
        Assert.True(replacement.BarrierSequence > first.BarrierSequence);
        scenario.ReportImpostorReady();

        // A complete, once-valid acknowledgement is insufficient after that attempt was canceled.
        scenario.Publish(scenario.Guest, first.ToApplied());
        // The candidate id in the payload does not authenticate the actual transport peer.
        scenario.Publish(scenario.Impostor, replacement.ToApplied());
        Assert.True(scenario.Identity.CurrentLease.IsPaused);
        Assert.Equal(2, scenario.Identity.Epoch);

        scenario.Publish(scenario.Guest, replacement.ToApplied());
        var accepted = scenario.Identity.CurrentLease;
        Assert.Equal("guest", accepted.AuthorityControllerId);
        Assert.Equal(3, accepted.Epoch);
        scenario.Publish(scenario.Guest, replacement.ToApplied());
        scenario.Publish(scenario.Guest, first.ToApplied());
        Assert.Equal(accepted, scenario.Identity.CurrentLease);
        Assert.Single(scenario.SentLeases, lease => !lease.IsPaused);
    }

    [Fact]
    public void CanceledFreezeAcknowledgementCannotCompleteANewerAttemptOrReviveOldAuthority()
    {
        using var scenario = new HandoffScenario(paused: false);
        scenario.SetHostMapReady(true);
        var first = scenario.LastSent<NetworkCampaignAuthorityFreezeRequest>();
        scenario.SetHostMapReady(false);
        Assert.Equal(first.HandoffSequence,
            scenario.LastSent<NetworkCampaignAuthorityFreezeCanceled>().HandoffSequence);
        scenario.SetHostMapReady(true);
        var replacement = scenario.LastSent<NetworkCampaignAuthorityFreezeRequest>();
        Assert.True(replacement.HandoffSequence > first.HandoffSequence);
        scenario.ReportImpostorReady();

        scenario.Publish(scenario.Guest, scenario.Acknowledge(first));
        scenario.Publish(scenario.Impostor, scenario.Acknowledge(replacement));
        Assert.Equal("guest", scenario.Identity.AuthorityControllerId);
        Assert.Equal(3, scenario.Identity.Epoch);
        Assert.Equal(0, scenario.AppliedRosters);

        scenario.Publish(scenario.Guest, scenario.Acknowledge(replacement));
        var accepted = scenario.Identity.CurrentLease;
        Assert.Equal("host", accepted.AuthorityControllerId);
        Assert.Equal(5, accepted.Epoch);
        Assert.Equal(1, scenario.AppliedRosters);

        scenario.Publish(scenario.Guest, scenario.Acknowledge(replacement));
        scenario.Publish(scenario.Guest, scenario.Acknowledge(first));
        Assert.Equal(accepted, scenario.Identity.CurrentLease);
        Assert.Equal(1, scenario.AppliedRosters);
        Assert.Equal(new[] { true, false }, scenario.SentLeases.Select(lease => lease.IsPaused));
    }

    [Fact]
    public void DisconnectedFrozenWriterWithoutFinalAcknowledgementCannotBeReplacedByTimeout()
    {
        using var scenario = new HandoffScenario(paused: false);
        scenario.SetHostMapReady(true);
        var request = scenario.LastSent<NetworkCampaignAuthorityFreezeRequest>();
        var oldLease = scenario.Identity.CurrentLease;
        var writerIdentity = new SessionIdentity();
        Assert.True(writerIdentity.TryApply(oldLease, "host"));
        var writerController = new ControllerIdProvider();
        writerController.SetControllerId("guest");
        var writerBroker = new MessageBroker();
        var writerNetwork = new Mock<INetwork>();
        using var writer = new CampaignAuthorityFreezeClientHandler(writerBroker, writerNetwork.Object,
            writerIdentity, writerController, scenario.Objects.Object, scenario.Rosters.Object,
            scenario.Clock.Object, scenario.Sessions.Object, CampaignHandoffTestServices.Scheduler(),
            CampaignHandoffTestServices.Drained());

        // Deliver the real request and deliberately withhold its emitted final ACK from the host.
        writerBroker.Publish(this, request);
        HandoffScenario.Drain();
        writerNetwork.Verify(network => network.SendAll(
            It.IsAny<NetworkCampaignAuthorityFreezeAcknowledged>()), Times.Once);
        Assert.True(writerIdentity.IsLocallyFrozen(oldLease));

        scenario.GuestConnected = false;
        scenario.Publish(scenario.Guest, new PlayerDisconnected(scenario.Guest, default));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            scenario.Now = scenario.Now.AddMinutes(1);
            scenario.Handler.RefreshLocalAvailabilityAndReconcile();
            HandoffScenario.Drain();
        }
        // This is a fenced unresolved handoff, not a successful replacement or automatic recovery.
        Assert.Equal(oldLease, scenario.Identity.CurrentLease);
        Assert.Empty(scenario.SentLeases);
        Assert.Equal(0, scenario.AppliedRosters);
        Assert.True(writerIdentity.IsLocallyFrozen(oldLease));
        bool wrote = false;
        Assert.False(writerIdentity.TryExecuteWithLease(oldLease, () => wrote = true));
        Assert.False(wrote);

        // Even a delayed ACK from the disconnected old peer cannot authorize the room host.
        scenario.Publish(scenario.Guest, scenario.Acknowledge(request));
        Assert.Equal(oldLease, scenario.Identity.CurrentLease);
        Assert.Empty(scenario.SentLeases);
    }

    private sealed class HandoffScenario : IDisposable
    {
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        private readonly MessageBroker broker = new MessageBroker();
        private readonly TestNetwork network = new TestNetwork();
        public readonly SessionIdentity Identity = new SessionIdentity();
        public readonly Mock<IObjectManager> Objects = new Mock<IObjectManager>();
        public readonly Mock<ICampaignRosterCheckpointService> Rosters = new Mock<ICampaignRosterCheckpointService>();
        public readonly Mock<IMapTimeTrackerInterface> Clock = new Mock<IMapTimeTrackerInterface>();
        public readonly Mock<ICampaignSessionCheckpointService> Sessions = new Mock<ICampaignSessionCheckpointService>();
        public readonly CampaignSimulationAvailabilityServerHandler Handler;
        public readonly NetPeer Guest;
        public readonly NetPeer Impostor;
        public bool GuestConnected = true;
        public int AppliedRosters;
        public DateTime Now = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        private readonly RegistryAllocationCheckpoint allocation = new RegistryAllocationCheckpoint(500, new Dictionary<string, int>());
        private readonly CampaignRosterCheckpoint roster = new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>());
        private readonly CampaignSessionCheckpoint session = new CampaignSessionCheckpoint("campaign", "{}");

        public HandoffScenario(bool paused)
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            Assert.True(Identity.TryApply(paused
                ? new AuthorityLease("room", "paused", string.Empty, 2, isPaused: true)
                : new AuthorityLease("room", "guest-lease", "guest", 3), "host"));
            Guest = network.CreatePeer();
            Impostor = network.CreatePeer("127.0.0.2");
            var host = new Player("host", "host-hero", "host-party", "host-clan", "host-character");
            var guest = new Player("guest", "guest-hero", "guest-party", "guest-clan", "guest-character");
            var impostor = new Player("other", "other-hero", "other-party", "other-clan", "other-character");
            var players = new Mock<IPlayerManager>();
            players.Setup(manager => manager.TryGetPlayer("host", out host)).Returns(true);
            players.Setup(manager => manager.TryGetPlayer(Guest, out guest)).Returns(true);
            players.Setup(manager => manager.TryGetPlayer(Impostor, out impostor)).Returns(true);
            players.Setup(manager => manager.TryGetPeer("guest", out It.Ref<NetPeer>.IsAny))
                .Returns((string _, out NetPeer peer) => { peer = Guest; return GuestConnected; });
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(value => value.HasCompletedCampaignSynchronization(Guest)).Returns(() => GuestConnected);
            connections.Setup(value => value.HasCompletedCampaignSynchronization(Impostor)).Returns(true);
            Objects.Setup(value => value.CaptureAllocationCheckpoint()).Returns(allocation);
            Objects.Setup(value => value.TryApplyAllocationCheckpoint(allocation)).Returns(true);
            var capturedRoster = roster;
            Rosters.Setup(value => value.TryCapture(out capturedRoster)).Returns(true);
            Rosters.Setup(value => value.TryApply(roster)).Callback(() => AppliedRosters++).Returns(true);
            long ticks = 999;
            Clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(true);
            Clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(ticks)).Returns(true);
            var capturedSession = session;
            Sessions.Setup(value => value.TryCapture(out capturedSession)).Returns(true);
            Sessions.Setup(value => value.TryApply(session)).Returns(true);
            var controller = new ControllerIdProvider();
            controller.SetControllerId("host");
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(Identity,
                new CampaignAuthorityCandidateSelector(), () => Now, TimeSpan.FromSeconds(3));
            Handler = new CampaignSimulationAvailabilityServerHandler(broker,
                coordinator, connections.Object, players.Object, controller,
                network, Identity, Objects.Object, rosterCheckpointService: Rosters.Object,
                mapTimeTracker: Clock.Object, sessionCheckpointService: Sessions.Object,
                schedulerCheckpointService: CampaignHandoffTestServices.Scheduler(),
                asyncTasks: CampaignHandoffTestServices.Drained());
            Publish(this, new CampaignReady());
            ReportGuest(CampaignSimulationAvailability.MapReady, 1);
            Assert.True(ModInformation.IsListenAuthority);
            Assert.True(connections.Object.HasCompletedCampaignSynchronization(Guest));
            Assert.True(players.Object.TryGetPlayer(Guest, out var registeredGuest));
            Assert.Equal("guest", registeredGuest.ControllerId);
            Assert.True(coordinator.IsFreshMapReady("guest"));
            Assert.Equal("guest", coordinator.Select().ControllerId);
        }

        public IEnumerable<NetworkAuthorityLeaseSnapshot> SentLeases =>
            network.SentNetworkMessages.TryGetValue(Guest.Id, out var messages)
                ? messages.OfType<NetworkAuthorityLeaseSnapshot>()
                : Enumerable.Empty<NetworkAuthorityLeaseSnapshot>();

        public T LastSent<T>() where T : IMessage => network.GetPeerMessagesFromType<T>(Guest).Last();

        public NetworkCampaignAuthorityFreezeAcknowledged Acknowledge(NetworkCampaignAuthorityFreezeRequest request) =>
            request.ToAcknowledged(allocation, roster, new CampaignClockCheckpoint(999), session,
                CampaignHandoffTestServices.Checkpoint());

        public void SetHostMapReady(bool ready) => Publish(this, new CampaignMapSimulationReadinessChanged(ready));

        public void ReportGuest(CampaignSimulationAvailability availability, long revision) =>
            Publish(Guest, new NetworkCampaignSimulationAvailabilityReport(availability, revision));

        public void ReportImpostorReady() => Publish(Impostor,
            new NetworkCampaignSimulationAvailabilityReport(CampaignSimulationAvailability.MapReady, 1));

        public void Publish<T>(object sender, T message) where T : IMessage
        {
            broker.Publish(sender, message);
            Drain();
        }

        public static void Drain() => GameThread.Run(() => { }, blocking: true);

        public void Dispose()
        {
            Handler.Dispose();
            Drain();
            ModInformation.SessionMode = previousMode;
        }
    }
}
