using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.ObjectManager;
using Moq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;
using Common;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using System.Runtime.CompilerServices;

namespace GameInterface.Tests.Services.MobileParties;

/// <summary>
/// Unit tests for the client send-side and room-host-to-authority routing of mercenary hires in
/// <see cref="MercenaryHireHandler"/>.
/// </summary>
public class MercenaryHireHandlerTests
{
    static MercenaryHireHandlerTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    }

    private readonly Mock<IMessageBroker> messageBroker = new();
    private readonly Mock<IObjectManager> objectManager = new();
    private readonly Mock<INetwork> network = new();
    private readonly Mock<ICampaignAuthorityContext> authorityContext = new();
    private readonly Mock<IPlayerManager> players = new();
    private readonly MercenaryHireHandler handler;

    private object? sentMessage;

    public MercenaryHireHandlerTests()
    {
        handler = new MercenaryHireHandler(
            messageBroker.Object,
            objectManager.Object,
            network.Object,
            authorityContext.Object,
            players.Object);

        network.Setup(n => n.SendAll(It.IsAny<IMessage>()))
            .Callback<IMessage>(message => sentMessage = message);
    }

    [Fact]
    public void Handle_MercenariesHired_ResolvableObjects_SendsRequestWithIdsCountAndGold()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var town = ObjectHelper.SkipConstructor<Town>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();
        hero.Gold = 1000;
        SetupId(hero, "hero-1");
        SetupId(party, "party-1");
        SetupId(town, "town-1");
        SetupId(troop, "troop-1");

        handler.Handle_MercenariesHired(Payload(hero, party, town, troop, count: 5, goldAmount: 250));

        var sent = Assert.IsType<HireMercenaries>(sentMessage!);
        Assert.Equal("hero-1", sent.MainHeroId);
        Assert.Equal("party-1", sent.MainPartyId);
        Assert.Equal("town-1", sent.TownId);
        Assert.Equal("troop-1", sent.MercenaryTroopId);
        Assert.Equal(5, sent.Count);
        Assert.Equal(250, sent.GoldAmount);
        Assert.Equal(1000, sent.HeroGold);
        Assert.True(sent.RequestSequence > 0);
        network.Verify(n => n.SendAll(It.IsAny<IMessage>()), Times.Once);
    }

    [Fact]
    public void Handle_MercenariesHired_IncludesConfirmedAuthorityLease()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        authorityContext.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var town = ObjectHelper.SkipConstructor<Town>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();
        SetupId(hero, "hero-1");
        SetupId(party, "party-1");
        SetupId(town, "town-1");
        SetupId(troop, "troop-1");

        handler.Handle_MercenariesHired(Payload(hero, party, town, troop, count: 2, goldAmount: 100));

        var request = Assert.IsType<HireMercenaries>(sentMessage!);
        Assert.True(request.MatchesLease(lease));
    }

    [Fact]
    public void Handle_HireRequest_RoutesOnceAndRejectsDuplicateSequence()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        authorityContext.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authorityContext.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return false;
            });

        using var peerFactory = new TestNetwork();
        NetPeer requesterPeer = peerFactory.CreatePeer();
        NetPeer authorityPeer = peerFactory.CreatePeer();
        var player = new Player("controller-requester", "hero-a", "party-a", "clan-a", "character-a");
        players.Setup(manager => manager.TryGetPlayer(requesterPeer, out player)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("controller-requester", out requesterPeer)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("controller-b", out authorityPeer)).Returns(true);
        var request = new HireMercenaries(
            "hero-a", "party-a", "town-a", "mercenary-a", 2, 60, 500,
            requestSequence: 1,
            lease: lease);

        handler.Handle_HireMercenaries(new MessagePayload<HireMercenaries>(requesterPeer, request));
        GameThread.Run(() => { }, blocking: true);
        handler.Handle_HireMercenaries(new MessagePayload<HireMercenaries>(requesterPeer, request));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(
            value => value.Send(authorityPeer, It.Is<NetworkCampaignAuthorityMercenaryHireCommand>(
                command => command.Matches(lease) && command.OriginControllerId == "controller-requester")),
            Times.Once);
    }

    [Fact]
    public void Handle_MercenariesHired_UnresolvableHero_DoesNotSend()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var town = ObjectHelper.SkipConstructor<Town>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();
        SetupNoId(hero);

        handler.Handle_MercenariesHired(Payload(hero, party, town, troop, count: 5, goldAmount: 250));

        Assert.Null(sentMessage);
        network.Verify(n => n.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void Handle_MercenariesHired_UnresolvableTroop_DoesNotSend()
    {
        var hero = ObjectHelper.SkipConstructor<Hero>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var town = ObjectHelper.SkipConstructor<Town>();
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();
        hero.Gold = 1000;
        SetupId(hero, "hero-1");
        SetupId(party, "party-1");
        SetupId(town, "town-1");
        SetupNoId(troop);

        handler.Handle_MercenariesHired(Payload(hero, party, town, troop, count: 5, goldAmount: 250));

        Assert.Null(sentMessage);
        network.Verify(n => n.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void CanApplyMercenaryHire_ValidCurrentStockAndGold_ReturnsTrue()
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: 1000,
            unitPrice: unitPrice,
            availableTroopMatches: true,
            availableMercenaries: 10);

        Assert.True(canApply);
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 4)]
    public void CanApplyMercenaryHire_StaleConversationStock_ReturnsFalse(bool availableTroopMatches, int availableMercenaries)
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: 1000,
            unitPrice: unitPrice,
            availableTroopMatches,
            availableMercenaries);

        Assert.False(canApply);
    }

    [Fact]
    public void CanApplyMercenaryHire_InsufficientCurrentServerGold_ReturnsFalse()
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: goldAmount - 1,
            unitPrice: unitPrice,
            availableTroopMatches: true,
            availableMercenaries: 10);

        Assert.False(canApply);
    }

    private MessagePayload<MercenariesHired> Payload(Hero hero, MobileParty party, Town town, CharacterObject troop, int count, int goldAmount) =>
        new(this, new MercenariesHired(hero, party, town, troop, count, goldAmount));

    private void SetupId(object obj, string id) =>
        objectManager.Setup(o => o.TryGetIdWithLogging(obj, out id)).Returns(true);

    private void SetupNoId(object obj)
    {
        string unused = string.Empty;
        objectManager.Setup(o => o.TryGetIdWithLogging(obj, out unused)).Returns(false);
    }
}
