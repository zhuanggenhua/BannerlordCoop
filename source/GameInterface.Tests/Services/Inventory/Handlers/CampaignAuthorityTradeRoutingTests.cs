using Common;
using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.Inventory.Handlers;
using GameInterface.Services.Inventory.Interfaces;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.Inventory.TradeSkills.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Tests;
using Moq;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.Inventory.Handlers;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityTradeRoutingTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 5);

    static CampaignAuthorityTradeRoutingTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void RoomHostRoutesTradeToCurrentRemoteAuthorityWithOriginAndLease()
    {
        using var network = new TestNetwork();
        var authorityPeer = network.CreatePeer();
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPeer("controller-b", out authorityPeer)).Returns(true);
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        authorityContext.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return true; });
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = default; return false; });
        var handler = CreateHandler(network, players.Object, authorityContext.Object);

        var previousMode = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;

            Assert.True(handler.TryRouteToAuthority(CreateTrade(), "controller-a", CurrentLease));

            var command = Assert.Single(network.GetPeerMessagesFromType<NetworkCampaignAuthorityCompleteTradeCommand>(authorityPeer));
            Assert.True(command.Matches(CurrentLease));
            Assert.Equal("controller-a", command.OriginControllerId);
            Assert.Equal("hero-a", command.Trade.HeroId);
            Assert.Equal("party-a", command.Trade.OwnerPartyId);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            handler.Dispose();
        }
    }

    [Fact]
    public void QueuedTradeRevalidatesTransportBeforeConsumingSequence()
    {
        var previousMode = ModInformation.SessionMode;
        using var peers = new TestNetwork();
        var old = peers.CreatePeer();
        var replacement = peers.CreatePeer();
        NetPeer current = old;
        var network = new Mock<INetwork>();
        network.As<IRoomHostPeerProvider>().Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
            .Returns<NetPeer>(peer => ReferenceEquals(peer, current));
        var host = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer("controller-a", out host)).Returns(true);
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return true; });
        var broker = new TestMessageBroker();
        var objects = new Mock<IObjectManager>();
        using var handler = new TradeHandler(Mock.Of<IInventoryLogicInterface>(), broker, objects.Object,
            network.Object, Mock.Of<IVillageHostileActionInterface>(), players.Object, context.Object,
            Mock.Of<IControllerIdProvider>(),
            Mock.Of<ISessionIdentity>(value => value.RoomHostControllerId == "controller-a"));
        var command = new NetworkCampaignAuthorityCompleteTradeCommand(CurrentLease, 1, "controller-a", CreateTrade());
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            GameThread.Run(() =>
            {
                System.Threading.Tasks.Task.Run(() => broker.Publish(old, command)).GetAwaiter().GetResult();
                current = replacement;
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);
            objects.Verify(value => value.TryGetObjectWithLogging<ItemRoster>(It.IsAny<string>(), out It.Ref<ItemRoster>.IsAny), Times.Never);
            broker.Publish(old, command);
            GameThread.Run(() => { }, blocking: true);
            objects.Verify(value => value.TryGetObjectWithLogging<ItemRoster>(It.IsAny<string>(), out It.Ref<ItemRoster>.IsAny), Times.Never);
            broker.Publish(replacement, command);
            GameThread.Run(() => { }, blocking: true);
            objects.Verify(value => value.TryGetObjectWithLogging<ItemRoster>("party-roster-a", out It.Ref<ItemRoster>.IsAny), Times.Once);
            Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
        }
        finally
        {
            handler.Dispose();
            GameThread.Run(() => { }, blocking: true);
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void AuthorityTradeSequenceRejectsReplaysAndResetsOnNewEpoch()
    {
        var handler = CreateHandler(Mock.Of<INetwork>(), Mock.Of<IPlayerManager>(), Mock.Of<ICampaignAuthorityContext>());
        var nextLease = new AuthorityLease("room-a", "authority-c", "controller-c", 6);
        try
        {
            Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
            Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
            Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 0));
            Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 2));
            Assert.True(handler.TryAdvanceAuthorityCommandSequence(nextLease, 1));
        }
        finally
        {
            handler.Dispose();
        }
    }

    [Fact]
    public void TradeOwnershipRequiresBothRegisteredHeroAndParty()
    {
        var player = new Player("controller-a", "hero-a", "party-a", "clan-a", "character-a");

        Assert.True(TradeHandler.PlayerOwnsTrade(player, CreateTrade()));
        Assert.False(TradeHandler.PlayerOwnsTrade(
            player,
            CreateTrade(heroId: "hero-b")));
        Assert.False(TradeHandler.PlayerOwnsTrade(
            player,
            CreateTrade(partyId: "party-b")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalMarketRosterCommitUsesAllowedThreadOnlyOnClients(bool isClient)
    {
        var roster = new ItemRoster();
        var elements = Array.Empty<ItemRosterElement>();
        var inventory = new Mock<IInventoryLogicInterface>();
        bool calledAllowed = false;
        inventory.Setup(logic => logic.UpdateRosterWithData(roster, elements))
            .Callback(() => calledAllowed = AllowedThread.IsThisThreadAllowed());

        TradeHandler.ApplyLocalMarketRosterUpdate(inventory.Object, roster, elements, isClient);

        Assert.Equal(isClient, calledAllowed);
        Assert.False(AllowedThread.IsThisThreadAllowed());
    }

    [Fact]
    public void AuthorityTradeCommandRoundTripsThroughTheMessageWireFormat()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var command = new NetworkCampaignAuthorityCompleteTradeCommand(
            CurrentLease,
            sequence: 19,
            originControllerId: "controller-a",
            CreateTrade());

        var received = Assert.IsType<NetworkCampaignAuthorityCompleteTradeCommand>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(command, serializer).Data));

        Assert.True(received.Matches(CurrentLease));
        Assert.Equal(19, received.Sequence);
        Assert.Equal("controller-a", received.OriginControllerId);
        Assert.Equal(command.Trade.RequestId, received.Trade.RequestId);
        Assert.Equal(command.Trade.HeroId, received.Trade.HeroId);
        Assert.Equal(command.Trade.OwnerPartyId, received.Trade.OwnerPartyId);
        Assert.Equal("ItemObject_trade_goods", Assert.Single(received.Trade.FromItemRosterData).ItemObjectData.ItemObjectId);
        Assert.Equal("ItemModifier_fine", Assert.Single(received.Trade.BoughtItems).Element.ItemObjectData.ItemModifierId);
        Assert.True(received.Trade.CharacterIdEquipmentsData["hero-a"][0].ItemSlots[0].HasItem);
    }

    [Fact]
    public void TradeSkillUpdateRoundTripsThroughTheMessageWireFormat()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var message = new NetworkUpdateTradeData(
            "hero-a",
            new[] { new TradeItemData(CreateItemData("trade_goods", 2), 300) },
            new[] { new TradeItemData(CreateItemData("trade_goods", 1), 180) },
            isTrading: true);

        var received = Assert.IsType<NetworkUpdateTradeData>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(message, serializer).Data));

        Assert.Equal("hero-a", received.PlayerHeroId);
        Assert.Equal("ItemObject_trade_goods", Assert.Single(received.PurchasedItems).Element.ItemObjectData.ItemObjectId);
        Assert.Equal(300, Assert.Single(received.PurchasedItems).TotalPrice);
        Assert.True(received.IsTrading);
    }

    private static TradeHandler CreateHandler(
        INetwork network,
        IPlayerManager players,
        ICampaignAuthorityContext authorityContext) => new(
            Mock.Of<IInventoryLogicInterface>(),
            Mock.Of<Common.Messaging.IMessageBroker>(),
            Mock.Of<IObjectManager>(),
            network,
            Mock.Of<IVillageHostileActionInterface>(),
            players,
            authorityContext);

    private static CompleteTrade CreateTrade(string heroId = "hero-a", string partyId = "party-a") => new(
        fromItemRosterId: null,
        isFromItemRosterNull: true,
        toItemRosterId: "party-roster-a",
        fromItemRosterData: new[] { CreateItemData("trade_goods", 3) },
        toItemRosterData: new[] { CreateItemData("trade_goods", 4) },
        characterIdEquipmentsData: new Dictionary<string, EquipmentData[]>
        {
            [heroId] = new[]
            {
                CreateEquipmentData(),
                new EquipmentData(default, new EquipmentSlotData[TaleWorlds.Core.Equipment.EquipmentSlotLength]),
                new EquipmentData(default, new EquipmentSlotData[TaleWorlds.Core.Equipment.EquipmentSlotLength])
            }
        },
        isTrading: true,
        canGainXpFromDiscarding: false,
        isManagingWarehouse: false,
        heroId: heroId,
        initialHeroId: heroId,
        totalAmount: 0,
        merchantGold: 0,
        ownerPartyId: partyId,
        currentMobilePartyId: null,
        isSettlementComponentNull: true,
        currentSettlementComponentId: null,
        boughtItems: new[] { new TradeItemData(CreateItemData("trade_goods", 2), 300) },
        soldItems: new[] { new TradeItemData(CreateItemData("trade_goods", 1), 180) },
        authorityLease: CurrentLease);

    private static ItemRosterElementData CreateItemData(string itemId, int amount) =>
        new(new ItemObjectData($"ItemObject_{itemId}", "ItemModifier_fine"), amount);

    private static EquipmentData CreateEquipmentData()
    {
        var slots = new EquipmentSlotData[TaleWorlds.Core.Equipment.EquipmentSlotLength];
        slots[0] = new EquipmentSlotData(new ItemObjectData("ItemObject_trade_goods", "ItemModifier_fine"));
        return new EquipmentData(default, slots);
    }
}
