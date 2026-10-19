using Common;
using Common.Messaging;
using Common.Network;
using Common.Tests.Utils;
using Common.Util;
using Coop.Core.Client.Services.MobileParties.Handlers;
using Coop.Core.Server.Services.MobileParties.Handlers;
using Coop.Core.Server.Services.MobileParties.Messages;
using Coop.Core.Server.Services.Settlements;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MapEvents;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using Moq;
using System;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace Coop.Tests.Client.Services.MobileParties;

public class SettlementLeaveDynamicRoutingTests
{
    [Theory]
    [InlineData("host-a", false)]
    [InlineData("", true)]
    public void LegacyClientOnlySendsForSessionWithoutListenHost(string roomHost, bool sendsLegacy)
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var objects = new Mock<IObjectManager>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        uint handle = 23;
        objects.Setup(value => value.TryGetHandleWithLogging(party, out handle)).Returns(true);
        var session = new SessionIdentity();
        Assert.True(session.TryApply(new AuthorityLease("room", "world", "writer", 5), roomHost));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("local");
        using var handler = new ClientSettlementExitEnterHandler(broker, network.Object, objects.Object,
            Mock.Of<ISettlementInterface>(), Mock.Of<IHideoutResultEncounter>(),
            new CampaignAuthorityContext(session, controller), session);

        broker.Publish(this, new EndSettlementEncounterAttempted(party));

        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestEndSettlementEncounter>()),
            sendsLegacy ? Times.Once : Times.Never);
    }

    [Fact]
    public void ListenHostLegacyRequestSubscriberDoesNotExecuteDynamicObserverRequest()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var settlements = new Mock<ISettlementInterface>();
        var session = new SessionIdentity();
        Assert.True(session.TryApply(new AuthorityLease("room", "world", "writer-b", 5), "host-a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("host-a");
        using var handler = new ServerSettlementExitEnterHandler(broker, network.Object,
            Mock.Of<IObjectManager>(), Mock.Of<IPlayerManager>(), settlements.Object,
            Mock.Of<IKingdomCreationSettlementTracker>(), Mock.Of<ISettlementEncounterDistanceValidator>(),
            new CampaignAuthorityContext(session, controller), session);

        broker.Publish(this, new NetworkRequestEndSettlementEncounter(23));
        GameThread.Run(() => { }, blocking: true);

        settlements.Verify(value => value.PartyLeaveSettlement(It.IsAny<MobileParty>()), Times.Never);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Fact]
    public void LocalWriterKeepsPopulationNotificationWithoutLegacyLeaveBroadcast()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var settlements = new Mock<ISettlementInterface>();
        var session = new SessionIdentity();
        Assert.True(session.TryApply(new AuthorityLease("room", "world", "host-a", 5), "host-a"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("host-a");
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        using var handler = new ServerSettlementExitEnterHandler(broker, network.Object,
            Mock.Of<IObjectManager>(), Mock.Of<IPlayerManager>(), settlements.Object,
            Mock.Of<IKingdomCreationSettlementTracker>(), Mock.Of<ISettlementEncounterDistanceValidator>(),
            new CampaignAuthorityContext(session, controller), session);

        broker.Publish(this, new PartyLeaveSettlementAttempted(party));

        settlements.Verify(value => value.OnPartyLeftSettlement(party), Times.Once);
        settlements.Verify(value => value.PartyLeaveSettlement(party), Times.Never);
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }
}
