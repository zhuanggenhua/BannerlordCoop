using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Serialization;
using Coop.Core.Client.Services.ItemRosters.Handlers;
using Coop.Core.Server.Services.ItemRosters.Messages;
using Coop.Core.Server.Services.Session;
using Coop.Core.Server.Connections;
using Coop.Tests.Mocks;
using GameInterface.Registry.Auto;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ItemObjects;
using GameInterface.Services.ItemRosters.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using Serilog;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Xunit;

namespace Coop.Tests.Server.Services.ItemRosters;

public sealed class CampaignItemRosterAuthorityTests
{
    [Fact]
    public void RelayRejectsStaleEpochAndReplayedAbsoluteInventoryResults()
    {
        var broker = new MessageBroker();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(new AuthorityLease("room", "world", "b", 3), "a"));
        var peer = new TestNetwork().CreatePeer();
        var player = new Player("b", "hero", "party", "clan", "character");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(peer, out player)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(true);
        var network = new Mock<INetwork>();
        using var handler = new CampaignAuthoritySyncRelayServerHandler(
            broker, connections.Object, players.Object, network.Object, identity, serializer);
        int applied = 0;
        broker.Subscribe<NetworkCampaignItemRosterCount>(_ => applied++);
        var data = serializer.Serialize(new NetworkCampaignItemRosterCount(11, 12, 0, "grain", 7));
        broker.Publish(peer, new NetworkCampaignAuthoritySyncEnvelope("room", "world", "b", 2, 9, data));
        var current = new NetworkCampaignAuthoritySyncEnvelope("room", "world", "b", 3, 1, data);
        broker.Publish(peer, current);
        broker.Publish(peer, current);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1, applied);
        network.Verify(value => value.SendAllBut(peer, It.IsAny<NetworkCampaignAuthoritySyncEnvelope>()), Times.Once);
    }

    [Fact]
    public void RemoteWriterPublishesAbsoluteCountWithStableItemIdentity()
    {
        using var fixture = new Fixture(true);
        fixture.Roster.AddToCounts(fixture.Element, 5);
        fixture.Broker.Publish(this, new ItemRosterUpdated(fixture.Roster, fixture.Item, null, 2));
        var result = Assert.Single(fixture.Sent);
        Assert.Equal(5, result.Count);
        Assert.Equal(11u, result.RosterId);
        Assert.Equal(12u, result.ItemId);
        Assert.Equal("grain", result.ItemStringId);
        Assert.False(result.Clear);
    }

    [Fact]
    public void ObserverDoesNotPublishEvenWhenTransportHost()
    {
        using var fixture = new Fixture(false);
        fixture.Broker.Publish(this, new ItemRosterUpdated(fixture.Roster, fixture.Item, null, 2));
        fixture.Broker.Publish(this, new ItemRosterCleared(fixture.Roster));
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public void ClearPublishesScopedResultAndReceiveDoesNotRebroadcast()
    {
        using var fixture = new Fixture(true);
        fixture.Broker.Publish(this, new ItemRosterCleared(fixture.Roster));
        var clear = Assert.Single(fixture.Sent);
        Assert.True(clear.Clear);
        int cleared = 0;
        fixture.Broker.Subscribe<ClearItemRoster>(_ => cleared++);
        fixture.Broker.Publish(this, clear);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1, cleared);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public void ReceivedCountIsAbsoluteAndDuplicateDeliveryDoesNotBecomeDelta()
    {
        using var fixture = new Fixture(false);
        var results = new System.Collections.Generic.List<UpdateItemRoster>();
        fixture.Broker.Subscribe<UpdateItemRoster>(payload => results.Add(payload.What));
        var count = new NetworkCampaignItemRosterCount(11, 12, 0, "grain", 7);
        fixture.Broker.Publish(this, count);
        fixture.Broker.Publish(this, count);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(2, results.Count);
        Assert.All(results, value => { Assert.True(value.IsAbsolute); Assert.Equal(7, value.Amount); });
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public void AbsoluteCountUsesExistingEpochEnvelopeContract()
    {
        IMessage value = new NetworkCampaignItemRosterCount(11, 12, 13, "grain", 17);
        Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(value);
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var copy = Assert.IsType<NetworkCampaignItemRosterCount>(serializer.Deserialize<IMessage>(serializer.Serialize(value)));
        Assert.Equal(17, copy.Count);
        Assert.Equal(13u, copy.ModifierId);
        Assert.Equal("grain", copy.ItemStringId);
    }

    private sealed class Fixture : System.IDisposable
    {
        public readonly MessageBroker Broker = new();
        public readonly ItemRoster Roster = new();
        public readonly ItemObject Item = new() { StringId = "grain" };
        public readonly EquipmentElement Element;
        public readonly System.Collections.Generic.List<NetworkCampaignItemRosterCount> Sent = new();
        private readonly CampaignItemRosterAuthorityHandler handler;
        public Fixture(bool writer)
        {
            Element = new EquipmentElement(Item);
            var authority = new Mock<ICampaignAuthorityContext>();
            authority.SetupGet(value => value.IsAuthorityKnown).Returns(true);
            var lease = new AuthorityLease("room", "world", "b", 3);
            authority.Setup(value => value.TryGetLocalAuthorityLease(out lease)).Returns(writer);
            var objects = new ObjectManager(Mock.Of<ILogger>());
            Assert.True(objects.AddExisting("inventory", Roster, 11));
            Assert.True(objects.AddExisting("ItemObject_grain", Item, 12));
            var items = new ItemObjectRegistry(Mock.Of<ILogger>(), Mock.Of<IAutoRegistryFactory>(), objects);
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(value => Sent.Add(Assert.IsType<NetworkCampaignItemRosterCount>(value)));
            handler = new CampaignItemRosterAuthorityHandler(Broker, network.Object, objects, items, authority.Object);
        }
        public void Dispose() => handler.Dispose();
    }
}
