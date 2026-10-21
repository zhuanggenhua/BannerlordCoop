using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Coop.Core.Client.Services.Instances;
using Coop.Core.Client.Services.Session;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Instances.Handlers;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Coop.Tests.Client.Services.Instances;

public class RoomMissionFactsProtocolTests
{
    private static RoomMissionFactsSnapshot Empty(string room, long revision = 0)
    {
        Assert.True(RoomMissionFactsSnapshot.TryCreate(room, revision, Array.Empty<RoomMissionMember>(), out var snapshot));
        return snapshot;
    }

    private static void Drain()
    {
        for (int i = 0; i < 4; i++) GameThread.Run(() => { }, blocking: true);
    }

    [Fact]
    public void FactsInstallOnGameThreadBeforeAckAndCanPrecedeInitialLeaseMetadata()
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var peer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        var identity = new SessionIdentity();
        var projection = new RoomMissionMembershipProjection();
        using var facts = new ClientRoomMissionFactsHandler(broker, network.Object, identity, projection);
        using var lease = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object, missionFacts: projection);
        var snapshot = Empty("room");
        var target = new AuthorityLease("room", "authority", "A", 1);
        network.Setup(value => value.SendAll(It.IsAny<NetworkRoomMissionFactsApplied>()))
            .Callback<IMessage>(message =>
            {
                Assert.True(GameThread.Instance.IsGameThread);
                Assert.True(projection.HasApplied(((NetworkRoomMissionFactsApplied)message).Version));
            });
        GameThread.Run(() =>
        {
            broker.Publish(peer, new NetworkRoomMissionFactsSnapshot(snapshot));
            Assert.False(projection.TryCapture(out _));
            broker.Publish(peer, new NetworkAuthorityLeaseSnapshot(target, "A", missionFactsVersion: snapshot.Version));
            Assert.False(identity.IsConfirmed);
        }, blocking: true);
        Drain();
        Assert.True(projection.HasApplied(snapshot.Version));
        Assert.Equal(target, identity.CurrentLease);
        Assert.True(identity.IsConfirmed);
        network.Verify(value => value.SendAll(It.IsAny<NetworkRoomMissionFactsApplied>()), Times.Once);
    }

    [Fact]
    public void InitialLeaseWaitsForExactAppliedFactsWithoutBusyLoopOrPrematureConfirmation()
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var peer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        var identity = new SessionIdentity();
        var projection = new RoomMissionMembershipProjection();
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object, missionFacts: projection);
        var snapshot = Empty("room");
        var target = new AuthorityLease("room", "authority", "A", 1);
        broker.Publish(peer, new NetworkAuthorityLeaseSnapshot(target, "A", missionFactsVersion: snapshot.Version));
        Drain();
        Assert.False(identity.IsConfirmed);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 0, Array.Empty<RoomMissionMember>(), out var exact));
        GameThread.Run(() => Assert.True(projection.TryInstallComplete(exact)), blocking: true);
        Drain();
        Assert.True(identity.IsConfirmed);
        Assert.Equal(target, identity.CurrentLease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedFactsFromReplacedPeerOrDisposedHandlerCannotInstallOrAck(bool dispose)
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var initial = new TestNetwork().CreatePeer();
        NetPeer current = initial;
        network.As<IRoomHostPeerProvider>().Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
            .Returns((NetPeer peer) => ReferenceEquals(peer, current));
        var projection = new RoomMissionMembershipProjection();
        using var handler = new ClientRoomMissionFactsHandler(broker, network.Object, new SessionIdentity(), projection);
        GameThread.Run(() =>
        {
            broker.Publish(initial, new NetworkRoomMissionFactsSnapshot(Empty("room")));
            if (dispose) handler.Dispose();
            else current = new TestNetwork().CreatePeer();
        }, blocking: true);
        Drain();
        Assert.False(projection.TryCapture(out _));
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void MissingVersionCannotConfirmEvenWhenCompleteEmptyFactsExist()
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var peer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        var identity = new SessionIdentity();
        var projection = new RoomMissionMembershipProjection();
        Assert.True(projection.TryInstallComplete(Empty("room")));
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object, missionFacts: projection);
        broker.Publish(peer, new NetworkAuthorityLeaseSnapshot(new AuthorityLease("room", "authority", "A", 1), "A"));
        Drain();
        Assert.False(identity.IsConfirmed);
    }

    [Fact]
    public void OccupiedMembershipCanDescribeObserverMetadataButCannotActivateLocalWriterWithoutProtection()
    {
        using var broker = new MessageBroker();
        var network = new Mock<INetwork>();
        var peer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        var identity = new SessionIdentity();
        var projection = new RoomMissionMembershipProjection();
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 1,
            new[] { new RoomMissionMember("A", "native-instance", 1) }, out var occupied));
        Assert.True(projection.TryInstallComplete(occupied));
        var controller = new ControllerIdProvider(); controller.SetControllerId("B");
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity,
            network: network.Object, controller: controller, missionFacts: projection);
        var target = new AuthorityLease("room", "authority-b", "B", 3);
        broker.Publish(peer, new NetworkAuthorityLeaseSnapshot(target, "A", missionFactsVersion: occupied.Version));
        Drain();
        Assert.False(identity.IsConfirmed);
        var observerLease = new AuthorityLease("room", "authority-a", "A", 3);
        broker.Publish(peer, new NetworkAuthorityLeaseSnapshot(observerLease, "A", missionFactsVersion: occupied.Version));
        Drain();
        Assert.True(identity.IsConfirmed);
        Assert.Equal(observerLease, identity.CurrentLease);
    }
}
