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
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Surrogates;
using LiteNetLib;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

public class CampaignAuthoritySyncRelayServerHandlerTests
{
    [Fact]
    public void RelaysAuthorityPartyBehaviorDeltaForCurrentRemoteLease()
    {
        _ = new SurrogateCollection();
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
        NetworkCampaignAuthorityPartyBehaviorDelta applied = default;
        object appliedSource = null;
        messageBroker.Subscribe<NetworkCampaignAuthorityPartyBehaviorDelta>(payload =>
        {
            applied = payload.What;
            appliedSource = payload.Who;
        });
        using var handler = new CampaignAuthoritySyncRelayServerHandler(
            messageBroker, connections.Object, playerManager.Object, network.Object, identity, serializer);
        var delta = new NetworkCampaignAuthorityPartyBehaviorDelta(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 });
        var envelope = new NetworkCampaignAuthoritySyncEnvelope(
            "room-a", "authority-b", "guest", 2, 1, serializer.Serialize(delta));

        messageBroker.Publish(guestPeer, envelope);

        Assert.Equal(42u, applied.BehaviorUpdateData.MobilePartyId);
        Assert.Same(guestPeer, appliedSource);
        network.Verify(value => value.SendAllBut(guestPeer, It.Is<NetworkCampaignAuthoritySyncEnvelope>(
            forwarded => forwarded.Sequence == 1 && forwarded.Epoch == 2)), Times.Once);
    }

    [Fact]
    public void RelaysRecruitmentRosterDeltaForCurrentRemoteLease()
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
        NetworkTroopRosterElementBatch applied = default;
        messageBroker.Subscribe<NetworkTroopRosterElementBatch>(payload => applied = payload.What);
        using var handler = new CampaignAuthoritySyncRelayServerHandler(
            messageBroker, connections.Object, playerManager.Object, network.Object, identity, serializer);
        var rosterDelta = new NetworkTroopRosterElementBatch(
            7, 9, new[] { TroopRosterElementOperation.AddCounts(1, 0, 0, removeDepleted: true) });
        var envelope = new NetworkCampaignAuthoritySyncEnvelope(
            "room-a", "authority-b", "guest", 2, 1, serializer.Serialize(rosterDelta));

        messageBroker.Publish(guestPeer, envelope);

        Assert.Equal(7u, applied.RosterId);
        Assert.Equal(9u, applied.CharacterId);
        Assert.Equal(1, Assert.Single(applied.Operations).Count);
        network.Verify(value => value.SendAllBut(guestPeer, It.Is<NetworkCampaignAuthoritySyncEnvelope>(
            forwarded => forwarded.Sequence == 1 && forwarded.Epoch == 2)), Times.Once);
    }

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
