using Coop.Tests.Mocks;
using Common;
using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Network.Session;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using Moq;
using Serilog;
using System.Collections.Generic;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class CampaignAuthorityHandoffBarrierClientHandlerTests
{
    [Fact]
    public void MatchesOnlyTheExactPausedLeaseAndLocalCandidate()
    {
        var lease = new AuthorityLease("room-a", "paused-authority", string.Empty, 7, isPaused: true);
        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            "room-a", "paused-authority", 7, "controller-b", 4);

        Assert.True(CampaignAuthorityHandoffBarrierClientHandler.Matches(barrier, lease, "controller-b"));
        Assert.False(CampaignAuthorityHandoffBarrierClientHandler.Matches(barrier, lease, "controller-c"));
        Assert.False(CampaignAuthorityHandoffBarrierClientHandler.Matches(
            new NetworkCampaignAuthorityHandoffBarrier("room-a", "old-authority", 7, "controller-b", 4),
            lease,
            "controller-b"));
        Assert.False(CampaignAuthorityHandoffBarrierClientHandler.Matches(
            new NetworkCampaignAuthorityHandoffBarrier("room-a", "paused-authority", 6, "controller-b", 4),
            lease,
            "controller-b"));
        Assert.False(CampaignAuthorityHandoffBarrierClientHandler.Matches(
            new NetworkCampaignAuthorityHandoffBarrier("room-a", "paused-authority", 7, "controller-b", 0),
            lease,
            "controller-b"));
        Assert.False(CampaignAuthorityHandoffBarrierClientHandler.Matches(
            barrier,
            new AuthorityLease("room-a", "active-authority", "controller-b", 7),
            "controller-b"));
    }

    [Fact]
    public void BarrierAndAcknowledgementRoundTripThroughTheMessageWireFormat()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var checkpoint = new RegistryAllocationCheckpoint(200, new Dictionary<string, int> { ["type"] = 90 });
        var rosters = new CampaignRosterCheckpoint(new[] { new CampaignRosterState(25, new CampaignRosterElementState[0]) });
        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            "room-a", "paused-authority", 7, "controller-b", 4, checkpoint, rosters, new CampaignClockCheckpoint(777),
            new CampaignSessionCheckpoint("campaign", "{}"), CampaignHandoffTestServices.Checkpoint());
        var acknowledgement = barrier.ToApplied();

        var receivedBarrier = Assert.IsType<NetworkCampaignAuthorityHandoffBarrier>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(barrier, serializer).Data));
        var receivedAcknowledgement = Assert.IsType<NetworkCampaignAuthorityHandoffBarrierApplied>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(acknowledgement, serializer).Data));

        Assert.Equal(barrier.RoomId, receivedBarrier.RoomId);
        Assert.Equal(barrier.PausedAuthorityId, receivedBarrier.PausedAuthorityId);
        Assert.Equal(barrier.PausedEpoch, receivedBarrier.PausedEpoch);
        Assert.Equal(barrier.CandidateControllerId, receivedBarrier.CandidateControllerId);
        Assert.Equal(barrier.BarrierSequence, receivedBarrier.BarrierSequence);
        Assert.Equal(200u, receivedBarrier.AllocationCheckpoint.NextHandle);
        Assert.Equal(90, receivedBarrier.AllocationCheckpoint.TypeCounters["type"]);
        Assert.Equal(25u, Assert.Single(receivedBarrier.RosterCheckpoint.Rosters).RosterHandle);
        Assert.Equal(777, receivedBarrier.ClockCheckpoint.Ticks);
        Assert.Equal("campaign", receivedBarrier.SessionCheckpoint.CampaignId);
        Assert.Equal("{}", receivedBarrier.SessionCheckpoint.SessionJson);
        Assert.Equal(50, receivedBarrier.SchedulerCheckpoint.MinimumIntervalTicks);
        Assert.Equal(barrier.RoomId, receivedAcknowledgement.RoomId);
        Assert.Equal(barrier.PausedAuthorityId, receivedAcknowledgement.PausedAuthorityId);
        Assert.Equal(barrier.PausedEpoch, receivedAcknowledgement.PausedEpoch);
        Assert.Equal(barrier.CandidateControllerId, receivedAcknowledgement.CandidateControllerId);
        Assert.Equal(barrier.BarrierSequence, receivedAcknowledgement.BarrierSequence);
    }

    [Fact]
    public void ClientAcknowledgesOnGameThreadAfterPreviouslyQueuedApplies()
    {
        var messageBroker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var sessionIdentity = new SessionIdentity();
        Assert.True(sessionIdentity.TryApply(
            new AuthorityLease("room-a", "paused-authority", string.Empty, 7, isPaused: true),
            "controller-a"));
        var controllerIdProvider = new ControllerIdProvider();
        controllerIdProvider.SetControllerId("controller-b");
        var objectManager = new ObjectManagerService(Mock.Of<ILogger>());
        var rosters = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        rosterService.Setup(value => value.TryApply(rosters)).Returns(true);
        bool clockApplied = false;
        bool sessionApplied = false;
        var sessionCheckpoint = new CampaignSessionCheckpoint("campaign", "{}");
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        sessions.Setup(value => value.TryApply(sessionCheckpoint)).Callback(() => sessionApplied = true).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(777))
            .Callback(() => { Assert.True(sessionApplied); clockApplied = true; }).Returns(true);
        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            "room-a", "paused-authority", 7, "controller-b", 4,
            new RegistryAllocationCheckpoint(200, new Dictionary<string, int>()), rosters, new CampaignClockCheckpoint(777), sessionCheckpoint, CampaignHandoffTestServices.Checkpoint());
        bool earlierApplyCompleted = false;
        NetworkCampaignAuthorityHandoffBarrierApplied? acknowledgement = null;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message =>
            {
                Assert.True(earlierApplyCompleted);
                Assert.True(clockApplied);
                Assert.Equal(200u, objectManager.CaptureAllocationCheckpoint().NextHandle);
                acknowledgement = Assert.IsType<NetworkCampaignAuthorityHandoffBarrierApplied>(message);
            });

        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(
            messageBroker, network.Object, sessionIdentity, controllerIdProvider, objectManager, rosterService.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        messageBroker.Publish(this, new ClientCampaignReady());
        messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));

        GameThread.EnqueueSafe(() => earlierApplyCompleted = true);
        messageBroker.Publish(this, barrier);

        // This blocking game-thread action is queued after the barrier callback. FIFO completion
        // proves the previous replica apply ran before the client emitted its acknowledgement.
        GameThread.Run(() => { }, blocking: true);

        Assert.True(earlierApplyCompleted);
        Assert.NotNull(acknowledgement);
        Assert.Equal(barrier.RoomId, acknowledgement.Value.RoomId);
        Assert.Equal(barrier.PausedAuthorityId, acknowledgement.Value.PausedAuthorityId);
        Assert.Equal(barrier.PausedEpoch, acknowledgement.Value.PausedEpoch);
        Assert.Equal(barrier.CandidateControllerId, acknowledgement.Value.CandidateControllerId);
        Assert.Equal(barrier.BarrierSequence, acknowledgement.Value.BarrierSequence);
        network.Verify(value => value.FlushPendingMessages(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidAllocationCheckpointCannotBeAcknowledged(bool malformed)
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("candidate");
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        var checkpoint = malformed
            ? new RegistryAllocationCheckpoint(300, new Dictionary<string, int> { ["type"] = -1 })
            : null;
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            manager, rosterService.Object, Mock.Of<IMapTimeTrackerInterface>(), Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1, checkpoint,
            clockCheckpoint: new CampaignClockCheckpoint(777)));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.Equal(1u, manager.CaptureAllocationCheckpoint().NextHandle);
        rosterService.Verify(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>()), Times.Never);
    }

    [Fact]
    public void FailedRosterReconciliationCannotBeAcknowledged()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("candidate");
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        rosterService.Setup(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>())).Returns(false);
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            manager, rosterService.Object, Mock.Of<IMapTimeTrackerInterface>(), Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1,
            new RegistryAllocationCheckpoint(50, new Dictionary<string, int>()), new CampaignRosterCheckpoint(new CampaignRosterState[0]),
            new CampaignClockCheckpoint(777)));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.True(identity.CurrentLease.IsPaused);
    }

    [Theory]
    [InlineData(false, 777L)]
    [InlineData(true, -1L)]
    [InlineData(true, 777L)]
    public void MissingInvalidOrUnappliedFinalClockCannotBeAcknowledged(bool includeClock, long ticks)
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("candidate");
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var rosters = new Mock<ICampaignRosterCheckpointService>();
        rosters.Setup(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>())).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(It.IsAny<long>())).Returns(false);
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        sessions.Setup(value => value.TryApply(It.IsAny<CampaignSessionCheckpoint>())).Returns(true);
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            manager, rosters.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1,
            new RegistryAllocationCheckpoint(50, new Dictionary<string, int>()), new CampaignRosterCheckpoint(new CampaignRosterState[0]),
            includeClock ? new CampaignClockCheckpoint(ticks) : null, schedulerCheckpoint: CampaignHandoffTestServices.Checkpoint()));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.True(identity.CurrentLease.IsPaused);
        clock.Verify(value => value.TryApplyAuthorityHandoffBaseline(It.IsAny<long>()),
            includeClock && ticks >= 0 ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrRejectedSessionCannotBeAcknowledged(bool includeSession)
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("candidate");
        var rosters = new Mock<ICampaignRosterCheckpointService>();
        rosters.Setup(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>())).Returns(true);
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        sessions.Setup(value => value.TryApply(It.IsAny<CampaignSessionCheckpoint>())).Returns(false);
        var clock = new Mock<IMapTimeTrackerInterface>();
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            new ObjectManagerService(Mock.Of<ILogger>()), rosters.Object, clock.Object, sessions.Object, CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained());
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1,
            new RegistryAllocationCheckpoint(50, new Dictionary<string, int>()), new CampaignRosterCheckpoint(new CampaignRosterState[0]),
            new CampaignClockCheckpoint(777), includeSession ? new CampaignSessionCheckpoint("campaign", "{}") : null, CampaignHandoffTestServices.Checkpoint()));
        GameThread.Run(() => { }, blocking: true);

        sessions.Verify(value => value.TryApply(It.IsAny<CampaignSessionCheckpoint>()), Times.Once);
        clock.Verify(value => value.TryApplyAuthorityHandoffBaseline(It.IsAny<long>()), Times.Never);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.True(identity.CurrentLease.IsPaused);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingOrRejectedSchedulerAndFailedAsyncDrainCannotBeAcknowledged(bool includeScheduler, bool drainSucceeds)
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "paused", "", 2, isPaused: true), "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("candidate");
        var rosters = new Mock<ICampaignRosterCheckpointService>();
        rosters.Setup(value => value.TryApply(It.IsAny<CampaignRosterCheckpoint>())).Returns(true);
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        sessions.Setup(value => value.TryApply(It.IsAny<CampaignSessionCheckpoint>())).Returns(true);
        var scheduler = new Mock<ICampaignSchedulerCheckpointService>();
        scheduler.Setup(value => value.TryApply(It.IsAny<CampaignSchedulerCheckpoint>())).Returns(false);
        var drain = new Mock<ICampaignAsyncTaskDrain>();
        drain.Setup(value => value.TryDrain()).Returns(drainSucceeds);
        var clock = new Mock<IMapTimeTrackerInterface>();
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            new ObjectManagerService(Mock.Of<ILogger>()), rosters.Object, clock.Object, sessions.Object, scheduler.Object, drain.Object);
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1,
            new RegistryAllocationCheckpoint(50, new Dictionary<string, int>()), new CampaignRosterCheckpoint(new CampaignRosterState[0]),
            new CampaignClockCheckpoint(777), new CampaignSessionCheckpoint("campaign", "{}"),
            includeScheduler ? CampaignHandoffTestServices.Checkpoint() : null));
        GameThread.Run(() => { }, blocking: true);
        scheduler.Verify(value => value.TryApply(It.IsAny<CampaignSchedulerCheckpoint>()),
            includeScheduler && drainSucceeds ? Times.Once() : Times.Never());
        clock.Verify(value => value.TryApplyAuthorityHandoffBaseline(It.IsAny<long>()), Times.Never);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.True(identity.CurrentLease.IsPaused);
    }
}
