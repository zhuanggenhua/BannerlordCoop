using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class CampaignFinalMovementProtocolTests
{
    private static readonly AuthorityLease Source = new("room", "writer", "b", 5);
    private static readonly AuthorityLease Paused = new("room", "paused", "", 6, true);
    private static CampaignMovementCheckpoint Checkpoint() => new(Source, 777, 1, new byte[] { 1, 2 }, "test-payload");

    [Theory]
    [InlineData("missing")]
    [InlineData("unprepared")]
    [InlineData("failed-install")]
    [InlineData("clock")]
    [InlineData("drain")]
    public void ObserverCannotConfirmPausedLeaseWithoutInstalledFinalMovement(string failure)
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Source, "a"));
        var checkpoint = failure == "missing" ? null : Checkpoint();
        Func<bool> install = () => failure != "failed-install";
        var movement = new Mock<ICampaignMovementCheckpointService>();
        movement.Setup(m => m.TryPrepare(checkpoint, Source, 777, out install)).Returns(failure != "unprepared");
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(c => c.TryApplyAuthorityHandoffBaseline(777)).Returns(failure != "clock");
        var tasks = Mock.Of<ICampaignAsyncTaskDrain>(t => t.TryDrain() == (failure != "drain"));
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, movement.Object, clock.Object, asyncTasks: tasks);
        broker.Publish(this, new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(Source, identity.CurrentLease);
        if (failure == "drain") movement.Verify(m => m.TryPrepare(It.IsAny<CampaignMovementCheckpoint>(), Source, 777, out install), Times.Never);
    }

    [Fact]
    public void ObserverInstallsFinalMovementBeforeConfirmingPausedLeaseEvenWithoutCandidateReadiness()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Source, "a"));
        var checkpoint = Checkpoint();
        bool installed = false;
        Func<bool> install = () => { Assert.Equal(Source, identity.CurrentLease); installed = true; return true; };
        var movement = new Mock<ICampaignMovementCheckpointService>();
        movement.Setup(m => m.TryPrepare(checkpoint, Source, 777, out install)).Returns(true);
        var clock = Mock.Of<IMapTimeTrackerInterface>(c => c.TryApplyAuthorityHandoffBaseline(777));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var network = new Mock<INetwork>();
        NetworkCampaignMovementBaselineApplied? ack = null;
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(m =>
        {
            Assert.True(installed);
            Assert.Equal(Paused, identity.CurrentLease);
            ack = Assert.IsType<NetworkCampaignMovementBaselineApplied>(m);
        });
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, movement.Object, clock, network.Object, controller, CampaignHandoffTestServices.Drained());
        broker.Publish(this, new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint));
        GameThread.Run(() => { }, blocking: true);
        Assert.True(installed);
        Assert.Equal(Paused, identity.CurrentLease);
        Assert.NotNull(ack);
        Assert.Equal(Paused.Epoch, ack.Value.PausedEpoch);
        Assert.Equal(checkpoint.Sha256, ack.Value.Sha256);
        Assert.Equal("b", ack.Value.ControllerId);
    }

    [Fact]
    public void StaleOrConflictingPausedLeaseCannotPrepareWorldWrites()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "new-writer", "a", 9), "a"));
        var movement = new Mock<ICampaignMovementCheckpointService>(MockBehavior.Strict);
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, movement.Object);
        broker.Publish(this, new NetworkAuthorityLeaseSnapshot(Paused, "a", Checkpoint()));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(9, identity.Epoch);
        movement.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnresolvedMovementRejectsBarrierBeforeOtherCheckpointMutation(bool missing)
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Paused, "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var registry = new Mock<IObjectManager>(MockBehavior.Strict);
        var network = new Mock<INetwork>();
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            registry.Object, Mock.Of<ICampaignRosterCheckpointService>(), Mock.Of<IMapTimeTrackerInterface>(),
            Mock.Of<ICampaignSessionCheckpointService>(), CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained(),
            Mock.Of<ICampaignMovementCheckpointService>());
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, Barrier(missing ? null : Checkpoint()));
        GameThread.Run(() => { }, blocking: true);
        network.Verify(n => n.SendAll(It.IsAny<IMessage>()), Times.Never);
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public void CandidateAcknowledgesOnlyAfterFinalMovementInstalledAndRejectsDuplicateBarrier()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Paused, "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var checkpoint = Checkpoint();
        var network = new Mock<INetwork>();
        var clock = Mock.Of<IMapTimeTrackerInterface>(c => c.TryApplyAuthorityHandoffBaseline(777));
        bool installed = false;
        Func<bool> apply = () => { installed = true; return true; };
        var movement = new Mock<ICampaignMovementCheckpointService>();
        movement.SetupGet(m => m.LastApplied).Returns(checkpoint);
        movement.Setup(m => m.TryPrepare(checkpoint, Source, 777, out apply)).Returns(true);
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback(() => Assert.True(installed));
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller,
            new ObjectManager(Mock.Of<ILogger>()), Mock.Of<ICampaignRosterCheckpointService>(r => r.TryApply(It.IsAny<CampaignRosterCheckpoint>())),
            clock, Mock.Of<ICampaignSessionCheckpointService>(s => s.TryApply(It.IsAny<CampaignSessionCheckpoint>())),
            CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained(), movement.Object);
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, Barrier(checkpoint));
        GameThread.Run(() => { }, blocking: true);
        broker.Publish(this, Barrier(checkpoint));
        GameThread.Run(() => { }, blocking: true);
        Assert.True(installed);
        network.Verify(n => n.SendAll(It.IsAny<NetworkCampaignAuthorityHandoffBarrierApplied>()), Times.Once);
    }

    [Fact]
    public void FrozenWriterCapturesAndSendsFinalMovementAfterWriteFreeze()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Source, "a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("b");
        var checkpoint = Checkpoint();
        var movement = new Mock<ICampaignMovementCheckpointService>();
        movement.Setup(m => m.TryCapture(Source, 777, out checkpoint))
            .Callback(() => Assert.True(identity.IsLocallyFrozen(Source))).Returns(true);
        var network = new Mock<INetwork>();
        NetworkCampaignAuthorityFreezeAcknowledged? ack = null;
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(m => ack = Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(m));
        var rosters = new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>());
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        rosterService.Setup(r => r.TryCapture(out rosters)).Returns(true);
        var session = new CampaignSessionCheckpoint("campaign", "{}");
        var sessions = new Mock<ICampaignSessionCheckpointService>();
        sessions.Setup(s => s.TryCapture(out session)).Returns(true);
        long ticks = 777;
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(c => c.TryGetCurrentTicks(out ticks)).Returns(true);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            new ObjectManager(Mock.Of<ILogger>()), rosterService.Object, clock.Object, sessions.Object,
            CampaignHandoffTestServices.Scheduler(), CampaignHandoffTestServices.Drained(), movement: movement.Object);
        broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(Source, "a", 44));
        GameThread.Run(() => { }, blocking: true);
        Assert.NotNull(ack);
        Assert.Same(checkpoint, ack.Value.MovementCheckpoint);
    }

    [Fact]
    public void FinalMovementRoundTripsInsideExistingOrderedMessageContracts()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var checkpoint = Checkpoint();
        IMessage[] messages = { Barrier(checkpoint), new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint),
            new NetworkCampaignAuthorityFreezeRequest(Source, "a", 44).ToAcknowledged(movementCheckpoint: checkpoint) };
        foreach (var message in messages)
        {
            var received = serializer.Deserialize<IMessage>(MessagePacket.Create(message, serializer).Data);
            var actual = received switch
            {
                NetworkCampaignAuthorityHandoffBarrier b => b.MovementCheckpoint,
                NetworkAuthorityLeaseSnapshot l => l.MovementCheckpoint,
                NetworkCampaignAuthorityFreezeAcknowledged f => f.MovementCheckpoint,
                _ => throw new InvalidOperationException(),
            };
            Assert.Equal(checkpoint.Payload, actual.Payload);
            Assert.Equal(checkpoint.SourceLease, actual.SourceLease);
            Assert.Equal(checkpoint.Ticks, actual.Ticks);
            Assert.Equal(checkpoint.Sha256, actual.Sha256);
        }
    }

    private static NetworkCampaignAuthorityHandoffBarrier Barrier(CampaignMovementCheckpoint checkpoint) =>
        new("room", "paused", 6, "b", 44, new RegistryAllocationCheckpoint(10, new Dictionary<string, int>()),
            new CampaignRosterCheckpoint(Array.Empty<CampaignRosterState>()), new CampaignClockCheckpoint(777),
            new CampaignSessionCheckpoint("campaign", "{}"), CampaignHandoffTestServices.Checkpoint(), checkpoint);
}
