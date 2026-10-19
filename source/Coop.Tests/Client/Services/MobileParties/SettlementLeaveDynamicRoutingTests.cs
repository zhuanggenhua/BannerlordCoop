using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.MobileParties;
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
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace Coop.Tests.Client.Services.MobileParties;

public class SettlementLeaveDynamicRoutingTests
{
    [Fact]
    public void ObserverLeaveQueuesReentrantStartUntilMatchingReplicaLeaveResolves()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var objects = new Mock<IObjectManager>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var settlement = ObjectHelper.SkipConstructor<TaleWorlds.CampaignSystem.Settlements.Settlement>();
        uint partyHandle = 23;
        uint settlementHandle = 31;
        objects.Setup(value => value.TryGetHandleWithLogging(party, out partyHandle)).Returns(true);
        objects.Setup(value => value.TryGetHandleWithLogging(settlement, out settlementHandle)).Returns(true);

        var lease = new AuthorityLease("room", "world", "writer", 5);
        var session = new SessionIdentity();
        Assert.True(session.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("observer");
        using var handler = new ClientSettlementExitEnterHandler(broker, network.Object, objects.Object,
            Mock.Of<ISettlementInterface>(), Mock.Of<IHideoutResultEncounter>(),
            new CampaignAuthorityContext(session, controller), session);

        // The observer end attempt is suppressed while the dynamic handler forms a correlated request.
        broker.Publish(this, new EndSettlementEncounterAttempted(party, lease));
        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Never);

        var request = new CampaignSettlementLeaveRequest(lease, "observer", 1, partyHandle, settlementHandle, "local-stream");
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            request, SettlementEncounterReplicaLeaveOutcome.Pending));
        DrainGameThread();
        broker.Publish(this, new StartSettlementEncounterAttempted(party, settlement));
        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Never);

        // An unrelated party, stale lease, or duplicate/older request cannot release this queue.
        var unrelated = new CampaignSettlementLeaveRequest(lease, "observer", 2, 99, settlementHandle, "other-stream");
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            unrelated, SettlementEncounterReplicaLeaveOutcome.Pending));
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            unrelated,
            SettlementEncounterReplicaLeaveOutcome.Applied));
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            new CampaignSettlementLeaveRequest(new AuthorityLease("room", "old-world", "writer", 4),
                "observer", 1, partyHandle, settlementHandle, "local-stream"),
            SettlementEncounterReplicaLeaveOutcome.Rejected));
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            new CampaignSettlementLeaveRequest(lease, "observer", 0, partyHandle, settlementHandle, "local-stream"),
            SettlementEncounterReplicaLeaveOutcome.Rejected));
        DrainGameThread();
        broker.Publish(this, new StartSettlementEncounterAttempted(party, settlement));
        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Never);

        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            request, SettlementEncounterReplicaLeaveOutcome.Applied));
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            request, SettlementEncounterReplicaLeaveOutcome.Applied));
        DrainGameThread();
        broker.Publish(this, new StartSettlementEncounterAttempted(party, settlement));
        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Once);
    }

    [Fact]
    public void DisposingBeforeQueuedLeaveResolutionDoesNotResumeAnotherPartyStart()
    {
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var objects = new Mock<IObjectManager>();
        var party = ObjectHelper.SkipConstructor<MobileParty>();
        var otherParty = ObjectHelper.SkipConstructor<MobileParty>();
        var settlement = ObjectHelper.SkipConstructor<TaleWorlds.CampaignSystem.Settlements.Settlement>();
        uint partyHandle = 23;
        uint otherPartyHandle = 24;
        uint settlementHandle = 31;
        objects.Setup(value => value.TryGetHandleWithLogging(party, out partyHandle)).Returns(true);
        objects.Setup(value => value.TryGetHandleWithLogging(otherParty, out otherPartyHandle)).Returns(true);
        objects.Setup(value => value.TryGetHandleWithLogging(settlement, out settlementHandle)).Returns(true);

        var lease = new AuthorityLease("room", "world", "writer", 5);
        var session = new SessionIdentity();
        Assert.True(session.TryApply(lease, "host"));
        var controller = new ControllerIdProvider();
        controller.SetControllerId("observer");
        var handler = new ClientSettlementExitEnterHandler(broker, network.Object, objects.Object,
            Mock.Of<ISettlementInterface>(), Mock.Of<IHideoutResultEncounter>(),
            new CampaignAuthorityContext(session, controller), session);

        var request = new CampaignSettlementLeaveRequest(lease, "observer", 1, partyHandle, settlementHandle, "local-stream");
        broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            request, SettlementEncounterReplicaLeaveOutcome.Pending));
        DrainGameThread();
        broker.Publish(this, new StartSettlementEncounterAttempted(otherParty, settlement));
        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Never);

        Task.Run(() => broker.Publish(this, new SettlementEncounterReplicaLeaveResolved(
            request, SettlementEncounterReplicaLeaveOutcome.Applied))).GetAwaiter().GetResult();
        handler.Dispose();
        DrainGameThread();

        network.Verify(value => value.SendAll(It.IsAny<NetworkRequestStartSettlementEncounter>()), Times.Never);
    }

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

    private static void DrainGameThread() => GameThread.Run(() => { }, blocking: true);
}
