using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Client.Services.TroopRosters.Messages;
using Coop.Core.Server.Services.TroopRosters.Handlers;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using LiteNetLib;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.TroopRosters;

public class ServerTroopRosterAuthorityRoutingTests
{
    [Fact]
    public void RejectsRecruitmentRequestFromAnOldAuthorityEpoch()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var authority = new Mock<ICampaignAuthorityContext>();
        authority.SetupGet(context => context.IsAuthorityKnown).Returns(true);
        authority.Setup(context => context.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        using var handler = CreateHandler(authority.Object, Mock.Of<IPlayerManager>(), Mock.Of<INetwork>());
        var staleRequest = new ClientRequestRecruitment(
            "party-a", new[] { new TroopInfo("hero-a", "troop-a", 0) }, 7,
            new AuthorityLease("room-a", "authority-a", "controller-a", 4));

        Assert.False(handler.TryGetRequestLease(staleRequest, out _));
        Assert.True(handler.TryGetRequestLease(new ClientRequestRecruitment(
            "party-a", new[] { new TroopInfo("hero-a", "troop-a", 0) }, 8, lease), out var matched));
        Assert.Equal(lease, matched);
    }

    [Fact]
    public void RelaysValidatedRecruitmentToRemoteAuthorityWithLeaseAndMonotonicSequence()
    {
        var authorityPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPeer("controller-b", out authorityPeer)).Returns(true);
        var network = new Mock<INetwork>();
        using var handler = CreateHandler(Mock.Of<ICampaignAuthorityContext>(), players.Object, network.Object);
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var request = new ClientRequestRecruitment(
            "party-a", new[] { new TroopInfo("hero-a", "troop-a", 0) }, 9, lease);

        Assert.True(handler.RouteToRemoteAuthority(request, "controller-a", lease));
        Assert.True(handler.RouteToRemoteAuthority(request, "controller-a", lease));

        network.Verify(value => value.Send(authorityPeer, It.Is<Common.Messaging.IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityRecruitmentCommand) &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).Matches(lease) &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).Sequence == 1 &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).OriginControllerId == "controller-a" &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).MobilePartyId == "party-a")), Times.Once);
        network.Verify(value => value.Send(authorityPeer, It.Is<Common.Messaging.IMessage>(message =>
            message.GetType() == typeof(NetworkCampaignAuthorityRecruitmentCommand) &&
            ((NetworkCampaignAuthorityRecruitmentCommand)message).Sequence == 2)), Times.Once);
    }

    private static ServerTroopRosterHandler CreateHandler(
        ICampaignAuthorityContext authorityContext,
        IPlayerManager players,
        INetwork network) => new(
            Mock.Of<IMessageBroker>(),
            network,
            Mock.Of<IObjectManager>(),
            Mock.Of<ITroopRosterInterface>(),
            players,
            authorityContext);
}
