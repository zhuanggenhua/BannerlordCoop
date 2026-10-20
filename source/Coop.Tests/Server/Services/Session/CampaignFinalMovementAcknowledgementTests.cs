using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Core.Server.States.Messages;
using Coop.Tests.Mocks;
using Coop.Tests.Extensions;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignFinalMovementAcknowledgementTests
{
    [Fact]
    public void CandidateBarrierWaitsForExactMemberMovementAcknowledgement()
    {
        using var fixture = new Fixture();
        fixture.Pause();
        var paused = fixture.Identity.CurrentLease;
        Assert.True(paused.IsPaused);
        Assert.Equal(1, fixture.Handler.PendingMovementAcknowledgements);
        Assert.DoesNotContain(fixture.Sent, m => m is NetworkCampaignAuthorityHandoffBarrier);
        var broadcast = Assert.Single(fixture.Sent.OfType<NetworkAuthorityLeaseSnapshot>(), s => s.IsPaused);
        Assert.Same(fixture.Checkpoint, broadcast.MovementCheckpoint);

        fixture.Broker.Publish(fixture.Peer, new NetworkCampaignMovementBaselineApplied(
            new AuthorityLease("room", "other-paused", "", paused.Epoch, true), "b", fixture.Checkpoint));
        fixture.Drain();
        Assert.Equal(1, fixture.Handler.PendingMovementAcknowledgements);
        fixture.Broker.Publish(fixture.Peer, new NetworkCampaignMovementBaselineApplied(paused, "wrong-controller", fixture.Checkpoint));
        fixture.Drain();
        Assert.Equal(1, fixture.Handler.PendingMovementAcknowledgements);
        fixture.Acknowledge(paused);
        Assert.Equal(0, fixture.Handler.PendingMovementAcknowledgements);
        var barrier = Assert.Single(fixture.Sent.OfType<NetworkCampaignAuthorityHandoffBarrier>());
        Assert.Same(fixture.Checkpoint, barrier.MovementCheckpoint);
        Assert.True(fixture.Identity.CurrentLease.IsPaused);
        fixture.Broker.Publish(fixture.Peer, barrier.ToApplied());
        fixture.Drain();
        Assert.False(fixture.Identity.CurrentLease.IsPaused);
        Assert.Equal("b", fixture.Identity.AuthorityControllerId);
    }

    [Fact]
    public void HostReturningCannotBypassUnacknowledgedObserverBaseline()
    {
        using var fixture = new Fixture();
        fixture.Pause();
        var paused = fixture.Identity.CurrentLease;
        fixture.Broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        fixture.Drain();
        Assert.Equal(paused, fixture.Identity.CurrentLease);
        fixture.Acknowledge(paused);
        Assert.False(fixture.Identity.CurrentLease.IsPaused);
        Assert.Equal("a", fixture.Identity.AuthorityControllerId);
    }

    [Fact]
    public void DisconnectedObserverDoesNotSubstituteForFinalMovementAcknowledgement()
    {
        using var fixture = new Fixture();
        fixture.Pause();
        var paused = fixture.Identity.CurrentLease;
        fixture.Connected = false;
        fixture.Broker.Publish(fixture.Peer, new PlayerDisconnected(fixture.Peer, default));
        fixture.Broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        fixture.Drain();
        Assert.Equal(paused, fixture.Identity.CurrentLease);
        Assert.Equal(1, fixture.Handler.PendingMovementAcknowledgements);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("authority")]
    [InlineData("epoch")]
    [InlineData("ticks")]
    [InlineData("hash")]
    [InlineData("controller")]
    [InlineData("non-peer")]
    [InlineData("same-id-different-peer")]
    [InlineData("unsynchronized")]
    public void RejectedAckPreservesPendingBaselineUntilExactOriginalMemberResponds(string fault)
    {
        using var fixture = new Fixture();fixture.Pause();var paused = fixture.Identity.CurrentLease;
        var wrongLease = new AuthorityLease(fault == "room" ? "wrong-room" : paused.RoomId,
            fault == "authority" ? "wrong-authority" : paused.AuthorityId, "", fault == "epoch" ? paused.Epoch + 1 : paused.Epoch, true);
        var baseline = new CampaignMovementCheckpoint(fixture.Checkpoint.SourceLease, fault == "ticks" ? 778 : 777, 1,
            fixture.Checkpoint.Payload, fault == "hash" ? "wrong-hash" : fixture.Checkpoint.Sha256);
        object sender = fixture.Peer;
        if (fault == "non-peer") sender = this;
        if (fault == "same-id-different-peer")
        {
            var replacement = new TestNetwork().CreatePeer();replacement.SetId(fixture.Peer.Id);sender = replacement;
            Assert.Equal(fixture.Peer.Id, replacement.Id);Assert.NotSame(fixture.Peer, replacement);
        }
        if (fault == "unsynchronized") fixture.Connected = false;
        fixture.Broker.Publish(sender, new NetworkCampaignMovementBaselineApplied(wrongLease, fault == "controller" ? "wrong-controller" : "b", baseline));
        fixture.Drain();
        Assert.Equal(paused, fixture.Identity.CurrentLease);Assert.Equal(1, fixture.Handler.PendingMovementAcknowledgements);
        Assert.DoesNotContain(fixture.Sent, message => message is NetworkCampaignAuthorityHandoffBarrier);
        fixture.Connected = true;fixture.Acknowledge(paused);
        Assert.Equal(0, fixture.Handler.PendingMovementAcknowledgements);
        Assert.Single(fixture.Sent.OfType<NetworkCampaignAuthorityHandoffBarrier>());
        fixture.Acknowledge(paused);
        Assert.Equal(0, fixture.Handler.PendingMovementAcknowledgements);
        Assert.Single(fixture.Sent.OfType<NetworkCampaignAuthorityHandoffBarrier>());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previousMode = ModInformation.SessionMode;
        public readonly TestMessageBroker Broker = new();
        public readonly SessionIdentity Identity = new();
        public readonly LiteNetLib.NetPeer Peer = new TestNetwork().CreatePeer();
        public readonly List<IMessage> Sent = new();
        public readonly CampaignSimulationAvailabilityServerHandler Handler;
        public readonly CampaignMovementCheckpoint Checkpoint;
        public bool Connected = true;

        public Fixture()
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            var source = new AuthorityLease("room", "writer", "a", 1);
            Assert.True(Identity.TryApply(source, "a"));
            Checkpoint = new CampaignMovementCheckpoint(source, 777, 1, new byte[] { 1 }, "native-test-hash");
            var movement = new Mock<ICampaignMovementCheckpointService>();
            var checkpoint = Checkpoint;
            movement.Setup(m => m.TryCapture(source, 777, out checkpoint)).Returns(true);
            movement.Setup(m => m.IsInstalled(checkpoint)).Returns(true);
            var coordinator = new CampaignAuthorityAvailabilityCoordinator(Identity, new CampaignAuthorityCandidateSelector());
            var players = new Mock<IPlayerManager>();
            var host = new Player("a", "ha", "pa", "ca", "oa");
            var guest = new Player("b", "hb", "pb", "cb", "ob");
            players.Setup(p => p.TryGetPlayer("a", out host)).Returns(true);
            players.Setup(p => p.TryGetPlayer(Peer, out guest)).Returns(true);
            var peer = Peer;
            players.Setup(p => p.TryGetPeer("b", out peer)).Returns(true);
            var connections = new Mock<IConnectionCollection>();
            connections.Setup(c => c.HasCompletedCampaignSynchronization(Peer)).Returns(() => Connected);
            var controller = new ControllerIdProvider();
            controller.SetControllerId("a");
            var network = new Mock<INetwork>();
            network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(Sent.Add);
            network.Setup(n => n.Send(Peer, It.IsAny<IMessage>())).Callback<LiteNetLib.NetPeer, IMessage>((_, m) => Sent.Add(m));
            var rosters = new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>());
            var rosterService = new Mock<ICampaignRosterCheckpointService>();
            rosterService.Setup(r => r.TryCapture(out rosters)).Returns(true);
            var session = new CampaignSessionCheckpoint("campaign", "{}");
            var sessions = new Mock<ICampaignSessionCheckpointService>();
            sessions.Setup(s => s.TryCapture(out session)).Returns(true);
            long ticks = 777;
            var clock = new Mock<IMapTimeTrackerInterface>();
            clock.Setup(c => c.TryGetCurrentTicks(out ticks)).Returns(true);
            Handler = new CampaignSimulationAvailabilityServerHandler(Broker, coordinator, connections.Object, players.Object,
                controller, network.Object, Identity, new ObjectManager(Serilog.Log.ForContext<ObjectManager>()),
                rosterCheckpointService: rosterService.Object, mapTimeTracker: clock.Object,
                sessionCheckpointService: sessions.Object, schedulerCheckpointService: CampaignHandoffTestServices.Scheduler(),
                asyncTasks: CampaignHandoffTestServices.Drained(), movement: movement.Object);
            Broker.Publish(this, new CampaignReady());
            Broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
            Broker.Publish(this, new CampaignInitializationCompleted());
            Broker.Publish(Peer, new NetworkCampaignSimulationAvailabilityReport(CampaignSimulationAvailability.MapReady, 1));
            Drain();
        }

        public void Pause()
        {
            Broker.Publish(this, new CampaignMapSimulationReadinessChanged(false));
            Drain();
        }
        public void Acknowledge(AuthorityLease paused)
        {
            Broker.Publish(Peer, new NetworkCampaignMovementBaselineApplied(paused, "b", Checkpoint));
            Drain();
        }
        public void Drain() => GameThread.Run(() => { }, blocking: true);
        public void Dispose()
        {
            Handler.Dispose();
            ModInformation.SessionMode = previousMode;
        }
    }
}
