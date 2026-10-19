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
using GameInterface.Services.MapEventParties;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party.Data;
using GameInterface.Services.Party.Handlers;
using GameInterface.Services.Party.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.TroopRosters.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Surrogates;
using Moq;
using LiteNetLib;
using GameInterface.Services.Players.Data;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.Party;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityPartyDoneLogicTests
{
    private static readonly AuthorityLease CurrentLease =
        new("room-a", "authority-b", "controller-b", 7);

    static CampaignAuthorityPartyDoneLogicTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
        _ = new SurrogateCollection();
    }

    [Fact]
    public void RoomHostRoutesPartyDoneLogicToCurrentRemoteAuthority()
    {
        using var network = new TestNetwork();
        var authorityPeer = network.CreatePeer();
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPeer("controller-b", out authorityPeer)).Returns(true);
        var authorityContext = CreateAuthorityContext();
        using var handler = CreateHandler(network, players.Object, authorityContext.Object);
        var previousMode = ModInformation.SessionMode;

        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;

            Assert.True(handler.TryRouteToAuthority(CreateRequest(), "controller-a", CurrentLease));

            var command = Assert.Single(network.GetPeerMessagesFromType<NetworkCampaignAuthorityPartyDoneLogicCommand>(authorityPeer));
            Assert.True(command.Matches(CurrentLease));
            Assert.Equal("controller-a", command.OriginControllerId);
            Assert.Equal("hero-a", command.Request.MainHeroId);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void AuthoritySequenceRejectsReplaysAndResetsForANewLease()
    {
        using var handler = CreateHandler(
            Mock.Of<INetwork>(),
            Mock.Of<IPlayerManager>(),
            Mock.Of<ICampaignAuthorityContext>());
        var nextLease = new AuthorityLease("room-a", "authority-c", "controller-c", 8);

        Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
        Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
        Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 0));
        Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 2));
        Assert.True(handler.TryAdvanceAuthorityCommandSequence(nextLease, 1));
    }

    [Fact]
    public void QueuedPartyCommandUsesLiveTransportAndRejectsSupersededPeerBeforeSequence()
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
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        hero._partyBelongedTo = party;
        var partyId = "party-a";
        var objects = new Mock<IObjectManager>();
        objects.Setup(value => value.TryGetObject("hero-a", out hero)).Returns(true);
        objects.Setup(value => value.TryGetId(party, out partyId)).Returns(true);
        var broker = new TestMessageBroker();
        using var handler = new PartyDoneLogicHandler(broker, objects.Object, network.Object,
            Mock.Of<ITroopRosterInterface>(), Mock.Of<IVillageHostileActionInterface>(), context.Object,
            players.Object, Mock.Of<IControllerIdProvider>(),
            Mock.Of<ISessionIdentity>(value => value.RoomHostControllerId == "controller-a"));
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var command = new NetworkCampaignAuthorityPartyDoneLogicCommand(CurrentLease, 1, "controller-a", CreateRequest());
            GameThread.Run(() =>
            {
                System.Threading.Tasks.Task.Run(() => broker.Publish(old, command)).GetAwaiter().GetResult();
                current = replacement;
            }, blocking: true);
            GameThread.Run(() => { }, blocking: true);
            Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 1));
            broker.Publish(old, new NetworkCampaignAuthorityPartyDoneLogicCommand(CurrentLease, 2, "controller-a", CreateRequest()));
            GameThread.Run(() => { }, blocking: true);
            Assert.True(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 2));
            broker.Publish(replacement, new NetworkCampaignAuthorityPartyDoneLogicCommand(CurrentLease, 3, "controller-a", CreateRequest()));
            GameThread.Run(() => { }, blocking: true);
            Assert.False(handler.TryAdvanceAuthorityCommandSequence(CurrentLease, 3));
        }
        finally
        {
            handler.Dispose();
            GameThread.Run(() => { }, blocking: true);
            ModInformation.SessionMode = previousMode;
        }
    }

    [Fact]
    public void PartyDoneLogicAuthorityCommandRoundTripsWithItemRosterData()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var request = CreateRequest();
        var command = new NetworkCampaignAuthorityPartyDoneLogicCommand(
            CurrentLease,
            sequence: 23,
            originControllerId: "controller-a",
            request);

        var received = Assert.IsType<NetworkCampaignAuthorityPartyDoneLogicCommand>(
            serializer.Deserialize<IMessage>(MessagePacket.Create(command, serializer).Data));

        Assert.True(received.Matches(CurrentLease));
        Assert.Equal(23, received.Sequence);
        Assert.Equal("controller-a", received.OriginControllerId);
        Assert.Equal(request.RequestId, received.Request.RequestId);
        Assert.Equal("ItemObject_trade_goods", Assert.Single(received.Request.RightOwnerPartyItemRosterData).ItemObjectData.ItemObjectId);
        Assert.Equal(7, Assert.Single(received.Request.RightOwnerPartyItemRosterData).Amount);
    }

    private static Mock<ICampaignAuthorityContext> CreateAuthorityContext()
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(value => value.IsAuthorityKnown).Returns(true);
        context.Setup(value => value.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = CurrentLease; return true; });
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) => { lease = default; return false; });
        return context;
    }

    private static PartyDoneLogicHandler CreateHandler(
        INetwork network,
        IPlayerManager playerManager,
        ICampaignAuthorityContext authorityContext) => new(
            Mock.Of<IMessageBroker>(),
            Mock.Of<IObjectManager>(),
            network,
            Mock.Of<ITroopRosterInterface>(),
            Mock.Of<IVillageHostileActionInterface>(),
            authorityContext,
            playerManager,
            Mock.Of<IControllerIdProvider>());

    private static NetworkCompleteDoneLogic CreateRequest() => new(
        "hero-a",
        Array.Empty<FlattenedTroop>(),
        Array.Empty<FlattenedTroop>(),
        Array.Empty<FlattenedTroop>(),
        EmptyRosterData(),
        EmptyRosterData(),
        EmptyRosterData(),
        EmptyRosterData(),
        new[] { new ItemRosterElementData(new ItemObjectData("ItemObject_trade_goods", null, true), 7) },
        new UpgradedTroopHistoryData(new List<UpgradedTroopHistoryElementData>()),
        leftPartyId: null,
        leftPrisonerRosterId: null,
        partyGoldChangeAmount: 0,
        partyInfluenceChangeAmount: 0,
        partyMoraleChangeAmount: 0,
        doNotApplyGoldTransactions: true,
        releaserPartyPosition: default,
        partyScreenMode: Helpers.PartyScreenHelper.PartyScreenMode.Normal,
        rightMemberOrderData: new TroopRosterOrderData(new()),
        authorityLease: CurrentLease);

    private static TroopRosterData EmptyRosterData() => new(Array.Empty<TroopRosterElementData>());
}
