using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class CampaignAuthorityFreezeClientHandlerTests
{
    [Fact]
    public void CurrentAuthorityFreezesBeforeSendingOrderedAcknowledgementAndCanBeCanceled()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
        Assert.True(identity.TryApply(lease, "controller-a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("controller-b");
        var network = new Mock<INetwork>();
        var operations = new List<string>();
        var coalescer = new Mock<ISendCoalescer>();
        coalescer.Setup(value => value.Flush(network.Object)).Callback(() =>
        {
            Assert.True(GameThread.Instance.IsGameThread);
            Assert.False(identity.IsLocallyFrozen(lease));
            Assert.True(identity.TryExecuteWithLease(lease, () => operations.Add("coalescer")));
        });
        var checkpoint = new RegistryAllocationCheckpoint(444, new Dictionary<string, int>());
        var rosters = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        rosterService.Setup(value => value.TryCapture(out rosters))
            .Callback(() => Assert.True(identity.IsLocallyFrozen(lease))).Returns(true);
        long finalTicks = 888;
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryGetCurrentTicks(out finalTicks))
            .Callback(() => Assert.True(identity.IsLocallyFrozen(lease))).Returns(true);
        var objectManager = new Mock<IObjectManager>();
        objectManager.Setup(value => value.CaptureAllocationCheckpoint())
            .Callback(() => Assert.True(identity.IsLocallyFrozen(lease)))
            .Returns(checkpoint);
        NetworkCampaignAuthorityFreezeAcknowledged? acknowledgement = null;

        network.Setup(value => value.FlushPendingMessages())
            .Callback(() => operations.Add("flush"));
        network.Setup(value => value.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message =>
            {
                Assert.True(identity.IsLocallyFrozen(lease));
                acknowledgement = Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(message);
                operations.Add("ack");
            });

        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            objectManager.Object, rosterService.Object, clock.Object, coalescer.Object);
        broker.Publish(this, request);
        GameThread.Run(() => { }, blocking: true);

        Assert.True(identity.IsLocallyFrozen(lease));
        Assert.NotNull(acknowledgement);
        Assert.Equal(new[] { "coalescer", "flush", "ack", "flush" }, operations);
        Assert.Equal(request.HandoffSequence, acknowledgement.Value.HandoffSequence);
        Assert.Equal(request.CandidateControllerId, acknowledgement.Value.CandidateControllerId);
        Assert.Same(checkpoint, acknowledgement.Value.AllocationCheckpoint);
        Assert.Same(rosters, acknowledgement.Value.RosterCheckpoint);
        Assert.Equal(888, acknowledgement.Value.ClockCheckpoint.Ticks);

        broker.Publish(this, request);
        GameThread.Run(() => { }, blocking: true);
        coalescer.Verify(value => value.Flush(network.Object), Times.Once);
        network.Verify(value => value.SendAll(It.IsAny<NetworkCampaignAuthorityFreezeAcknowledged>()), Times.Exactly(2));

        broker.Publish(this, request.ToCanceled());
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsLocallyFrozen(lease));
    }

    [Fact]
    public void FailedRosterCaptureKeepsOldAuthorityFrozenWithoutAcknowledgement()
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        CampaignRosterCheckpoint checkpoint = null;
        rosterService.Setup(value => value.TryCapture(out checkpoint)).Returns(false);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosterService.Object, Mock.Of<IMapTimeTrackerInterface>());
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33);
        broker.Publish(this, request);
        GameThread.Run(() => { }, blocking: true);

        Assert.True(identity.IsLocallyFrozen(lease));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        broker.Publish(this, request.ToCanceled());
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsLocallyFrozen(lease));
    }

    [Fact]
    public void FreezeRequestMustMatchTheExactActiveAuthorityLeaseAndLocalController()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);

        Assert.True(CampaignAuthorityFreezeClientHandler.Matches(request, lease, "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(request, lease, "controller-a"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            new NetworkCampaignAuthorityFreezeRequest(lease, "controller-b", 44), lease, "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            request,
            new AuthorityLease("room-a", "authority-c", "controller-b", 4),
            "controller-b"));
        Assert.False(CampaignAuthorityFreezeClientHandler.Matches(
            new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 0),
            lease,
            "controller-b"));
    }

    [Theory]
    [InlineData(false, 888L)]
    [InlineData(true, -1L)]
    public void MissingOrInvalidFrozenClockPreventsAcknowledgement(bool clockAvailable, long ticks)
    {
        var broker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room", "authority", "writer", 2);
        Assert.True(identity.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("writer");
        var network = new Mock<INetwork>();
        var rosterService = new Mock<ICampaignRosterCheckpointService>();
        var roster = new CampaignRosterCheckpoint(new CampaignRosterState[0]);
        rosterService.Setup(value => value.TryCapture(out roster)).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryGetCurrentTicks(out ticks)).Returns(clockAvailable);
        using var handler = new CampaignAuthorityFreezeClientHandler(broker, network.Object, identity, controller,
            Mock.Of<IObjectManager>(), rosterService.Object, clock.Object);
        broker.Publish(this, new NetworkCampaignAuthorityFreezeRequest(lease, "host", 33));
        GameThread.Run(() => { }, blocking: true);
        Assert.True(identity.IsLocallyFrozen(lease));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void FreezeRequestAcknowledgementAndCancellationRoundTripThroughTheMessageWireFormat()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var checkpoint = new RegistryAllocationCheckpoint(555, new Dictionary<string, int> { ["type"] = 800 });
        var rosters = new CampaignRosterCheckpoint(new[] { new CampaignRosterState(88, new CampaignRosterElementState[0]) });
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 3);
        var request = new NetworkCampaignAuthorityFreezeRequest(lease, "controller-a", 44);

        var receivedRequest = Assert.IsType<NetworkCampaignAuthorityFreezeRequest>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request, serializer).Data));
        var receivedAcknowledgement = Assert.IsType<NetworkCampaignAuthorityFreezeAcknowledged>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request.ToAcknowledged(checkpoint, rosters,
                new CampaignClockCheckpoint(888)), serializer).Data));
        var receivedCancellation = Assert.IsType<NetworkCampaignAuthorityFreezeCanceled>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(request.ToCanceled(), serializer).Data));

        Assert.Equal(request.RoomId, receivedRequest.RoomId);
        Assert.Equal(request.AuthorityId, receivedRequest.AuthorityId);
        Assert.Equal(request.AuthorityControllerId, receivedRequest.AuthorityControllerId);
        Assert.Equal(request.Epoch, receivedRequest.Epoch);
        Assert.Equal(request.CandidateControllerId, receivedRequest.CandidateControllerId);
        Assert.Equal(request.HandoffSequence, receivedRequest.HandoffSequence);
        Assert.Equal(request.HandoffSequence, receivedAcknowledgement.HandoffSequence);
        Assert.Equal(555u, receivedAcknowledgement.AllocationCheckpoint.NextHandle);
        Assert.Equal(800, receivedAcknowledgement.AllocationCheckpoint.TypeCounters["type"]);
        Assert.Equal(88u, Assert.Single(receivedAcknowledgement.RosterCheckpoint.Rosters).RosterHandle);
        Assert.Equal(888, receivedAcknowledgement.ClockCheckpoint.Ticks);
        Assert.Equal(request.HandoffSequence, receivedCancellation.HandoffSequence);
    }
}
