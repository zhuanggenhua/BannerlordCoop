using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.PacketHandlers;
using Common.Util;
using Coop.Core.Server.Services.MobileParties.PacketHandlers;
using Coop.Core.Server.Services.MobileParties.Packets;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Connections;

public class RemoteMovementOwnershipTests
{
    [Fact]
    public void RoomHostDoesNotApplyMovementWhenConfirmedLeaseBelongsToAnotherController()
    {
        var peer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("B", "HeroB", "PartyB", "ClanB", "CharacterB");
        var players = new Mock<IPlayerManager>();
        players.Setup(p => p.TryGetPlayer(peer, out player)).Returns(true);
        players.Setup(p => p.TryGetPeer("B", out peer)).Returns(true);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns(false);
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(), Mock.Of<IPartyBehaviorWireMapper>(), players.Object, authority.Object);
        var data = new PartyBehaviorUpdateData("PartyB", default, null, default, default, default, default, default);

        Assert.False(handler.TryAuthorize(peer, ref data));
    }

    [Fact]
    public void RoomHostRoutesPlayerBehaviorRequestToRemoteAuthorityWithLeaseAndSequence()
    {
        var authorityPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPeer("authority-b", out authorityPeer)).Returns(true);
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var data = new PartyBehaviorUpdateData("PartyA", default, null, default, default, default, default, default)
        { OriginControllerId = "host-a" };
        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 17 };
        wireMapper.Setup(mapper => mapper.TryToNetwork(data, out networkData)).Returns(true);
        var network = new Mock<INetwork>();
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            network.Object, wireMapper.Object, players.Object);
        var lease = new AuthorityLease("room-a", "authority-b", "authority-b", 4);

        Assert.True(handler.RouteToRemoteAuthority(data, lease));
        Assert.True(handler.RouteToRemoteAuthority(data, lease));

        network.Verify(value => value.Send(authorityPeer, It.Is<Common.Messaging.IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityPartyBehaviorCommand) &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).RoomId == "room-a" &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).AuthorityId == "authority-b" &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).Epoch == 4 &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).Sequence == 1 &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).OriginControllerId == "host-a")), Times.Once);
        network.Verify(value => value.Send(authorityPeer, It.Is<Common.Messaging.IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityPartyBehaviorCommand) &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).Sequence == 2)), Times.Once);
    }

    [Fact]
    public void RequestLeaseValidationRejectsAnOldEpoch()
    {
        var currentLease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authority.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = currentLease;
                return true;
            });
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(), Mock.Of<IPartyBehaviorWireMapper>(), Mock.Of<IPlayerManager>(), authority.Object);

        var stale = new RequestMobilePartyBehaviorPacket(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 17 },
            requestSequence: 9,
            lease: new AuthorityLease("room-a", "authority-a", "controller-a", 4));
        var current = new RequestMobilePartyBehaviorPacket(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 17 },
            requestSequence: 10,
            lease: currentLease);

        Assert.False(handler.TryGetRequestLease(stale, out _));
        Assert.True(handler.TryGetRequestLease(current, out var matchedLease));
        Assert.Equal(currentLease, matchedLease);
    }

    [Fact]
    public void LegacyUnconfirmedSessionStillAcceptsUnfencedRequestLease()
    {
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.SetupGet(context => context.IsAuthorityKnown).Returns(false);
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(), Mock.Of<IPartyBehaviorWireMapper>(), Mock.Of<IPlayerManager>(), authority.Object);

        Assert.True(handler.TryGetRequestLease(new RequestMobilePartyBehaviorPacket(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 17 }), out _));
    }

    [Fact]
    public void ListenHostRoutesItsLocalPlayerBehaviorRequestToRemoteAuthority()
    {
        var authorityPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("host-a", "hero-a", "party-a", "clan-a", "character-a");
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPlayer("host-a", out player)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("authority-b", out authorityPeer)).Returns(true);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authority.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = new AuthorityLease("room-a", "authority-b", "authority-b", 4);
                return false;
            });
        var wireMapper = new Mock<IPartyBehaviorWireMapper>();
        var data = new PartyBehaviorUpdateData("party-a", default, null, default, default, default, default, default)
        { OriginControllerId = "host-a" };
        var networkData = new NetworkPartyBehaviorUpdateData { MobilePartyId = 17 };
        wireMapper.Setup(mapper => mapper.TryToNetwork(data, out networkData)).Returns(true);
        var network = new Mock<INetwork>();
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            network.Object, wireMapper.Object, players.Object, authority.Object);

        handler.Handle_ControlledPartyBehaviorUpdated(new MessagePayload<ControlledPartyBehaviorUpdated>(
            this,
            new ControlledPartyBehaviorUpdated(data)));

        network.Verify(value => value.Send(authorityPeer, It.Is<Common.Messaging.IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityPartyBehaviorCommand) &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).OriginControllerId == "host-a" &&
            ((NetworkCampaignAuthorityPartyBehaviorCommand)message).Sequence == 1)), Times.Once);
    }

    [Theory]
    [InlineData("PartyB", false, true, true)]
    [InlineData("PartyA", false, true, false)]
    [InlineData("PartyB", true, true, false)]
    [InlineData("PartyB", false, false, false)]
    public void OnlyCurrentPeerMayMoveItsOwnPartyWithoutTeleporting(
        string partyId, bool teleport, bool current, bool accepted)
    {
        var peer = ObjectHelper.SkipConstructor<NetPeer>();
        var oldPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("B", "HeroB", "PartyB", "ClanB", "CharacterB");
        var players = new Mock<IPlayerManager>();
        players.Setup(p => p.TryGetPlayer(peer, out player)).Returns(true);
        var binding = current ? peer : oldPeer;
        players.Setup(p => p.TryGetPeer("B", out binding)).Returns(true);
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(), Mock.Of<IPartyBehaviorWireMapper>(), players.Object);
        var data = new PartyBehaviorUpdateData(partyId, default, null, default, default, default, default, default)
        { OriginControllerId = "forged-A", ForcePosition = teleport };
        Assert.Equal(accepted, handler.TryAuthorize(peer, ref data));
        if (accepted) Assert.Equal("B", data.OriginControllerId);
        Assert.False(handler.TryAuthorize(null, ref data));
    }
}
