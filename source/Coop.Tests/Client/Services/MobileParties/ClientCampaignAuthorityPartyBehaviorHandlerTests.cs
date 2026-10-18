using Common.Messaging;
using Common;
using Common.Network;
using Common.PacketHandlers;
using Coop.Core.Client.Services.MobileParties.Handlers;
using Coop.Core.Server.Services.MobileParties.Packets;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using Moq;
using Xunit;

namespace Coop.Tests.Client.Services.MobileParties;

public class ClientCampaignAuthorityPartyBehaviorHandlerTests
{
    [Fact]
    public void LocalAuthoritySendsScopedPartyBehaviorDelta()
    {
        var broker = new Mock<IMessageBroker>();
        var network = new Mock<INetwork>();
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });

        var source = new PartyBehaviorUpdateData();
        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 };
        wireMapper.Setup(mapper => mapper.TryToNetwork(source, out networkData)).Returns(true);
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, network.Object, wireMapper.Object, authorityContext.Object);

        handler.HandleAuthorityUpdate(new MessagePayload<PartyBehaviorUpdated>(
            source,
            new PartyBehaviorUpdated(ref source)));

        network.Verify(network => network.SendAll(It.Is<IMessage>(message =>
            message is NetworkCampaignAuthorityPartyBehaviorDelta)), Times.Once);
    }

    [Fact]
    public void ObserverDoesNotPublishAuthorityPartyBehaviorDelta()
    {
        var broker = new Mock<IMessageBroker>();
        var network = new Mock<INetwork>();
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = default;
                return false;
            });
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, network.Object, wireMapper.Object, authorityContext.Object);
        var source = new PartyBehaviorUpdateData();

        handler.HandleAuthorityUpdate(new MessagePayload<PartyBehaviorUpdated>(
            source,
            new PartyBehaviorUpdated(ref source)));

        network.Verify(network => network.SendAll(It.IsAny<IMessage>()), Times.Never);
        wireMapper.VerifyNoOtherCalls();
    }

    [Fact]
    public void RemoteAuthorityRequestCarriesTheConfirmedLease()
    {
        var broker = new Mock<IMessageBroker>();
        var network = new Mock<INetwork>();
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        authorityContext.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authorityContext.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });

        var source = new PartyBehaviorUpdateData();
        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 };
        wireMapper.Setup(mapper => mapper.TryToNetwork(source, out networkData)).Returns(true);
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, network.Object, wireMapper.Object, authorityContext.Object);

        handler.Handle(new MessagePayload<ControlledPartyBehaviorUpdated>(
            source,
            new ControlledPartyBehaviorUpdated(source)));

        network.Verify(value => value.SendAll(It.Is<IPacket>(message =>
            message.GetType() == typeof(RequestMobilePartyBehaviorPacket) &&
            ((RequestMobilePartyBehaviorPacket)message).MatchesLease(lease) &&
            ((RequestMobilePartyBehaviorPacket)message).RequestSequence == 1)), Times.Once);
    }

    [Fact]
    public void RemoteAuthorityRequestIsDroppedWhileLeaseIsPaused()
    {
        var broker = new Mock<IMessageBroker>();
        var network = new Mock<INetwork>();
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var pausedLease = new AuthorityLease("room-a", "authority-b", string.Empty, 3, isPaused: true);
        authorityContext.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authorityContext.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = pausedLease;
                return true;
            });
        var source = new PartyBehaviorUpdateData();
        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 };
        wireMapper.Setup(mapper => mapper.TryToNetwork(source, out networkData)).Returns(true);
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, network.Object, wireMapper.Object, authorityContext.Object);

        handler.Handle(new MessagePayload<ControlledPartyBehaviorUpdated>(
            source,
            new ControlledPartyBehaviorUpdated(source)));

        network.Verify(value => value.SendAll(It.IsAny<IPacket>()), Times.Never);
    }

    [Fact]
    public void LocalAuthorityAppliesFreshRoutedPlayerCommandOnlyOnce()
    {
        var broker = new Mock<IMessageBroker>();
        var network = new Mock<INetwork>();
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });

        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 };
        var behaviorData = new PartyBehaviorUpdateData();
        wireMapper.Setup(mapper => mapper.TryFromNetwork(networkData, out behaviorData)).Returns(true);
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, network.Object, wireMapper.Object, authorityContext.Object);
        var source = new object();
        var command = new NetworkCampaignAuthorityPartyBehaviorCommand(
            lease, 1, "controller-a", networkData);
        var payload = new MessagePayload<NetworkCampaignAuthorityPartyBehaviorCommand>(source, command);

        handler.HandleAuthorityCommand(payload);
        handler.HandleAuthorityCommand(payload);
        GameThread.Run(() => { }, blocking: true);

        broker.Verify(messageBroker => messageBroker.Publish(
            source,
            It.Is<UpdatePartyBehavior>(update =>
                !update.IsAuthorityReplay && update.AlreadyOnGameThread &&
                update.BehaviorUpdateData.OriginControllerId == "controller-a")), Times.Once);
    }

    [Fact]
    public void OldEpochCommandQueuedBehindPauseCannotResolveOrApplyObjects()
    {
        var broker = new Mock<IMessageBroker>();
        var mapper = new Mock<IPartyBehaviorWireMapper>();
        var context = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        bool active = true;
        context.Setup(value => value.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) => { current = lease; return active; });
        var wireData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 };
        var data = new PartyBehaviorUpdateData();
        mapper.Setup(value => value.TryFromNetwork(wireData, out data)).Returns(true);
        using var handler = new ClientMobilePartyBehaviorHandler(
            broker.Object, Mock.Of<INetwork>(), mapper.Object, context.Object);
        using var entered = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        GameThread.EnqueueSafe(() => { entered.Set(); release.Wait(System.TimeSpan.FromSeconds(5)); });
        try
        {
            Assert.True(entered.Wait(System.TimeSpan.FromSeconds(5)));
            GameThread.EnqueueSafe(() => active = false);
            handler.HandleAuthorityCommand(new MessagePayload<NetworkCampaignAuthorityPartyBehaviorCommand>(
                this, new NetworkCampaignAuthorityPartyBehaviorCommand(lease, 1, "controller-a", wireData)));
        }
        finally { release.Set(); }
        GameThread.Run(() => { }, blocking: true);
        mapper.Verify(value => value.TryFromNetwork(wireData, out It.Ref<PartyBehaviorUpdateData>.IsAny), Times.Never);
        broker.Verify(value => value.Publish(It.IsAny<object>(), It.IsAny<UpdatePartyBehavior>()), Times.Never);
    }
}
