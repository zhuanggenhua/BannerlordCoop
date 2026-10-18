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
        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            "room-a", "paused-authority", 7, "controller-b", 4, checkpoint);
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
        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            "room-a", "paused-authority", 7, "controller-b", 4,
            new RegistryAllocationCheckpoint(200, new Dictionary<string, int>()));
        bool earlierApplyCompleted = false;
        NetworkCampaignAuthorityHandoffBarrierApplied? acknowledgement = null;
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message =>
            {
                Assert.True(earlierApplyCompleted);
                Assert.Equal(200u, objectManager.CaptureAllocationCheckpoint().NextHandle);
                acknowledgement = Assert.IsType<NetworkCampaignAuthorityHandoffBarrierApplied>(message);
            });

        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(
            messageBroker, network.Object, sessionIdentity, controllerIdProvider, objectManager);
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
        var checkpoint = malformed
            ? new RegistryAllocationCheckpoint(300, new Dictionary<string, int> { ["type"] = -1 })
            : null;
        using var handler = new CampaignAuthorityHandoffBarrierClientHandler(broker, network.Object, identity, controller, manager);
        broker.Publish(this, new ClientCampaignReady());
        broker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        broker.Publish(this, new NetworkCampaignAuthorityHandoffBarrier("room", "paused", 2, "candidate", 1, checkpoint));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        Assert.Equal(1u, manager.CaptureAllocationCheckpoint().NextHandle);
    }
}
