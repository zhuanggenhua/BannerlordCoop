using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Client.Services.TroopRosters.Messages;
using Coop.Core.Server.Services.TroopRosters.Handlers;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using LiteNetLib;
using Moq;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace Coop.Tests.Server.Services.TroopRosters;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ListenHostRecruitmentTests : IDisposable
{
    private readonly SessionMode previous = ModInformation.SessionMode;
    private readonly AuthorityLease lease = new("room-a", "authority-b", "b", 5);
    private readonly Mock<IPlayerManager> players = new();
    private readonly Mock<IObjectManager> objects = new();
    private readonly Mock<INetwork> network = new();
    private readonly Mock<ITroopRosterInterface> roster = new();
    private readonly Mock<ICampaignAuthorityContext> authority = new();
    private Player local = new("a", "hero-a", "MobileParty_party-a", "clan-a", "char-a");
    private MobileParty party = ObjectHelper.SkipConstructor<MobileParty>();
    private NetPeer authorityPeer = new TestNetwork().CreatePeer();
    private readonly ControllerIdProvider controller = new();

    public ListenHostRecruitmentTests()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        controller.SetControllerId("a");
        players.Setup(value => value.TryGetPlayer("a", out local)).Returns(true);
        players.Setup(value => value.TryGetPeer("b", out authorityPeer)).Returns(true);
        objects.Setup(value => value.TryGetObject("MobileParty_party-a", out party)).Returns(true);
        authority.SetupGet(value => value.IsAuthorityKnown).Returns(true);
        authority.Setup(value => value.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) => { current = lease; return true; });
    }

    public void Dispose() => ModInformation.SessionMode = previous;

    private ServerTroopRosterHandler Create(IMessageBroker broker) => new(broker, network.Object, objects.Object,
        roster.Object, players.Object, authority.Object, controller);

    [Fact]
    public void HostObserverRoutesItsOwnCartToBWithoutClientOrLoopback()
    {
        var notable = ObjectHelper.SkipConstructor<Hero>();
        var troop = new CharacterObject();
        string notableId = "notable", troopId = "troop";
        objects.Setup(value => value.TryGetIdWithLogging(notable, out notableId)).Returns(true);
        objects.Setup(value => value.TryGetIdWithLogging(troop, out troopId)).Returns(true);
        using var broker = new MessageBroker();
        using var handler = Create(broker);

        broker.Publish(this, new RecruitmentAttempted(party, new[] { (notable, troop, 0) }, lease));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.Send(authorityPeer, It.Is<IMessage>(message =>
            message is NetworkCampaignAuthorityRecruitmentCommand &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).Matches(lease) &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).OriginControllerId == "a" &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).MobilePartyId == local.MobilePartyId)), Times.Once);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        roster.Verify(value => value.HandleOnRecruitmentDone(It.IsAny<string>(), It.IsAny<TroopInfo[]>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostObserverRejectsDifferentLocalPartyOrAnOvertakenEpoch(bool wrongParty)
    {
        var notable = ObjectHelper.SkipConstructor<Hero>();
        var troop = new CharacterObject();
        using var handler = Create(Mock.Of<IMessageBroker>());
        var stale = new AuthorityLease("room-a", "authority-a", "a", 4);
        handler.HandleLocalRecruitment(new(this, new RecruitmentAttempted(
            wrongParty ? ObjectHelper.SkipConstructor<MobileParty>() : party,
            new[] { (notable, troop, 0) }, wrongParty ? lease : stale)));
        GameThread.Run(() => { }, blocking: true);

        network.Verify(value => value.Send(It.IsAny<NetPeer>(), It.IsAny<IMessage>()), Times.Never);
        roster.Verify(value => value.HandleOnRecruitmentDone(It.IsAny<string>(), It.IsAny<TroopInfo[]>()), Times.Never);
    }

    [Fact]
    public void RemoteRequestRequiresCurrentPeerAndCanonicalPartyThenExecutesOnce()
    {
        var requestPeer = new TestNetwork().CreatePeer();
        var player = new Player("b", "hero-b", "MobileParty_party-b", "clan-b", "char-b");
        players.Setup(value => value.TryGetPlayer(requestPeer, out player)).Returns(true);
        players.Setup(value => value.TryGetPeer("b", out requestPeer)).Returns(true);
        authority.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) => { current = lease; return true; });
        authority.Setup(value => value.TryExecuteWithCurrentLease(lease, It.IsAny<Action>()))
            .Returns((AuthorityLease _, Action action) => { action(); return true; });
        using var handler = Create(Mock.Of<IMessageBroker>());
        var request = new ClientRequestRecruitment("party-b", new[] { new TroopInfo("notable", "troop", 0) }, 1, lease);

        handler.HandleOnRecruitmentDone(new(requestPeer, request));
        handler.HandleOnRecruitmentDone(new(requestPeer, request));
        handler.HandleOnRecruitmentDone(new(new TestNetwork().CreatePeer(), request));
        GameThread.Run(() => { }, blocking: true);

        roster.Verify(value => value.HandleOnRecruitmentDone("party-b", It.IsAny<TroopInfo[]>()), Times.Once);
    }
}
