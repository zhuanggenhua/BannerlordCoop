using Common;
using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Network.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Services.Session;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using Xunit;
using Moq;
using System;
using System.Collections.Generic;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class AuthorityLeaseDistributionHandlerTests
{
    [Fact]
    public void CommittedPausedLeaseIsSentToARejoiningCandidate()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            using var broker = new TestMessageBroker();
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("room", "committed-paused", string.Empty, 8, isPaused: true);
            Assert.True(identity.TryApply(lease, "controller-a"));
            var network = new Mock<INetwork>();
            var terminator = new Mock<IJoinPeerTerminator>();
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var peer = new TestNetwork().CreatePeer();
            using var handler = new AuthorityLeaseDistributionHandler(broker, network.Object, identity, controller, terminator.Object);
            broker.Publish(this, new PlayerConnected(peer));
            network.Verify(value => value.SendImmediate(peer, It.Is<NetworkAuthorityLeaseSnapshot>(snapshot => snapshot.ToLease() == lease)), Times.Once);
            terminator.Verify(value => value.Disconnect(It.IsAny<LiteNetLib.NetPeer>(), It.IsAny<string>()), Times.Never);
        }
        finally { ModInformation.SessionMode = previousMode; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialLeaseSurvivesPreSaveDroppingUnlessTheRemoteWriterCheckpointIsUnavailable(bool alreadyMigrated)
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var broker = new TestMessageBroker();
            var network = new Mock<INetwork>();
            var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
            using var queue = new ConnectionMessageQueue(new Lazy<INetwork>(() => network.Object), broker, serializer);
            var delivered = new List<IMessage>();
            var peer = new TestNetwork().CreatePeer();
            network.Setup(value => value.Send(peer, It.IsAny<IMessage>())).Callback<LiteNetLib.NetPeer, IMessage>((p, message) =>
            {
                if (!queue.TryHandleBroadcast(p, MessagePacket.Create(message, serializer))) delivered.Add(message);
            });
            network.Setup(value => value.SendImmediate(peer, It.IsAny<IMessage>()))
                .Callback<LiteNetLib.NetPeer, IMessage>((_, message) => delivered.Add(message));
            var identity = new SessionIdentity();
            var expectedLease = new AuthorityLease("room", "authority-b", "controller-b", 7);
            if (alreadyMigrated) Assert.True(identity.TryApply(expectedLease, "controller-a"));
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            var terminator = new Mock<IJoinPeerTerminator>();
            using var handler = new AuthorityLeaseDistributionHandler(broker, network.Object, identity, controller, terminator.Object);

            broker.Publish(this, new PlayerConnected(peer));

            if (alreadyMigrated)
            {
                Assert.Empty(delivered);
                Assert.Equal(expectedLease, identity.CurrentLease);
                terminator.Verify(value => value.Disconnect(peer, "CampaignCheckpointUnavailable"), Times.Once);
                return;
            }

            var snapshot = Assert.IsType<NetworkAuthorityLeaseSnapshot>(Assert.Single(delivered));
            Assert.Equal(identity.CurrentLease, snapshot.ToLease());
            Assert.Equal(alreadyMigrated ? "controller-b" : "controller-a", snapshot.AuthorityControllerId);
            if (alreadyMigrated) Assert.Equal(expectedLease, identity.CurrentLease);
            network.Verify(value => value.Send(peer, It.IsAny<IMessage>()), Times.Never);
        }
        finally { ModInformation.SessionMode = previousMode; }
    }

    [Fact]
    public void ListenAuthority_SendsItsLocalControllerInTheLeaseSnapshot()
    {
        var previousMode = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var messageBroker = new TestMessageBroker();
            var network = new TestNetwork();
            var peer = network.CreatePeer();
            var identity = new SessionIdentity();
            var controller = new ControllerIdProvider();
            controller.SetControllerId("controller-a");
            using var handler = new AuthorityLeaseDistributionHandler(
                messageBroker, network, identity, controller);

            messageBroker.Publish(new object(), new CampaignReady());
            messageBroker.Publish(new object(), new PlayerConnected(peer));

            var snapshot = Assert.Single(network.GetPeerMessagesFromType<NetworkAuthorityLeaseSnapshot>(peer));
            Assert.Equal(identity.RoomId, snapshot.RoomId);
            Assert.Equal(identity.AuthorityId, snapshot.AuthorityId);
            Assert.Equal("controller-a", snapshot.AuthorityControllerId);
            Assert.Equal("controller-a", snapshot.RoomHostControllerId);
            Assert.Equal(1, snapshot.Epoch);
            Assert.True(identity.IsConfirmed);
            Assert.Equal("controller-a", identity.RoomHostControllerId);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void Client_AppliesInitialSnapshotAndRejectsStaleEpoch()
    {
        var messageBroker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var snapshotNetwork = new Mock<INetwork>();var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(snapshotNetwork);
        using var handler = new AuthorityLeaseSnapshotHandler(messageBroker, identity, network: snapshotNetwork.Object);
        var snapshot = new NetworkAuthorityLeaseSnapshot(
            new AuthorityLease("room-a", "authority-a", "controller-a", 1));

        messageBroker.Publish(roomPeer, snapshot);
        messageBroker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(
            new AuthorityLease("room-a", "authority-stale", "controller-stale", 0)));
        GameThread.Run(() => { }, blocking: true);

        Assert.Equal("room-a", identity.RoomId);
        Assert.Equal("authority-a", identity.AuthorityId);
        Assert.Equal("controller-a", identity.AuthorityControllerId);
        Assert.Equal(1, identity.Epoch);
    }

    [Fact]
    public void Client_AppliesPausedLeaseAndRejectsPreviousActiveLease()
    {
        var messageBroker = new TestMessageBroker();
        var identity = new SessionIdentity();
        var snapshotNetwork = new Mock<INetwork>();var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(snapshotNetwork);
        using var handler = new AuthorityLeaseSnapshotHandler(messageBroker, identity, network: snapshotNetwork.Object);
        var activeLease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
        messageBroker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(activeLease, "controller-a"));

        var pausedLease = new AuthorityLease("room-a", "authority-paused", string.Empty, 2, isPaused: true);
        messageBroker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(pausedLease, "controller-a"));
        messageBroker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(activeLease, "controller-a"));

        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(pausedLease, identity.CurrentLease);
        Assert.True(identity.CurrentLease.IsPaused);
        Assert.Equal(2, identity.Epoch);
    }
}
