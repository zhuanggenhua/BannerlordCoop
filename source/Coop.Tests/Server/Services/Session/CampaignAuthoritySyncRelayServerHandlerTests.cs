using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

public class CampaignAuthoritySyncRelayServerHandlerTests
{
    [Fact]
    public void AppliesAndRelaysOnlyCurrentAuthorityDeltasWithIncreasingSequence()
    {
        var messageBroker = new TestMessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-b", "guest", 2), "host"));
        var peerFactory = new TestNetwork();
        NetPeer guestPeer = peerFactory.CreatePeer();
        var player = new Player("guest", "hero-guest", "party-guest", "clan-guest", "character-guest");
        var playerManager = new Mock<IPlayerManager>();
        playerManager.Setup(manager => manager.TryGetPlayer(guestPeer, out player)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(guestPeer)).Returns(true);
        var network = new Mock<INetwork>();
        var appliedValues = new System.Collections.Generic.List<int>();
        object appliedSource = null;
        messageBroker.Subscribe<Coop.Tests.Network.CoopNetworkBaseTests.AuthorityProbeMessage>(
            payload =>
            {
                appliedValues.Add(payload.What.Value);
                appliedSource = payload.Who;
            });

        using var handler = new CampaignAuthoritySyncRelayServerHandler(
            messageBroker, connections.Object, playerManager.Object, network.Object, identity, serializer);

        var validEnvelope = CreateEnvelope(serializer, epoch: 2, authorityId: "authority-b", controllerId: "guest", sequence: 1, value: 42);
        messageBroker.Publish(guestPeer, validEnvelope);
        messageBroker.Publish(guestPeer, validEnvelope);
        messageBroker.Publish(guestPeer,
            CreateEnvelope(serializer, epoch: 1, authorityId: "authority-old", controllerId: "guest", sequence: 99, value: 99));
        messageBroker.Publish(guestPeer,
            CreateEnvelope(serializer, epoch: 2, authorityId: "authority-b", controllerId: "other-player", sequence: 2, value: 77));

        Assert.Equal(new[] { 42 }, appliedValues);
        Assert.Same(guestPeer, appliedSource);
        network.Verify(value => value.SendAllBut(guestPeer, It.Is<NetworkCampaignAuthoritySyncEnvelope>(
            envelope => envelope.Sequence == 1 && envelope.Epoch == 2)), Times.Once);
    }

    private static NetworkCampaignAuthoritySyncEnvelope CreateEnvelope(
        ICommonSerializer serializer,
        long epoch,
        string authorityId,
        string controllerId,
        long sequence,
        int value) => new NetworkCampaignAuthoritySyncEnvelope(
            "room-a",
            authorityId,
            controllerId,
            epoch,
            sequence,
            serializer.Serialize(new Coop.Tests.Network.CoopNetworkBaseTests.AuthorityProbeMessage { Value = value }));
}
