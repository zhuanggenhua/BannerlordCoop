using Common;
using Common.Network;
using Common.PacketHandlers;
using Coop.Core.Common.Network.Packets;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Time.Handlers;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System;
using Xunit;

namespace Coop.Tests.Server.Services.Time;

public sealed class CampaignAuthorityTimePacketRelayHandlerTests
{
    [Fact]
    public void RemoteAuthorityClock_IsAppliedLocallyAndRelayedWithPerPeerJoinProgress()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 4);
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(lease, "controller-a"));
        var peerFactory = new TestNetwork();
        var authorityPeer = peerFactory.CreatePeer();
        var observerPeer = peerFactory.CreatePeer("127.0.0.2");
        var player = new Player("controller-b", "hero-b", "party-b", "clan-b", "character-b");

        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(authorityPeer, out player)).Returns(true);
        players.Setup(value => value.TryGetPeer("controller-b", out authorityPeer)).Returns(true);

        var authorityConnection = Mock.Of<IConnectionLogic>(value => value.Peer == authorityPeer);
        var observerConnection = Mock.Of<IConnectionLogic>(value => value.Peer == observerPeer);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(authorityPeer)).Returns(true);
        connections.As<IEnumerable<IConnectionLogic>>()
            .Setup(value => value.GetEnumerator())
            .Returns(new[] { authorityConnection, observerConnection }.AsEnumerable().GetEnumerator());

        var catchupQueue = new Mock<IConnectionMessageQueue>();
        catchupQueue.Setup(value => value.TryGetCatchUpPacketsRemaining(
                observerPeer, out It.Ref<int>.IsAny))
            .Returns((NetPeer _, out int remaining) => { remaining = 17; return true; });

        var packetManager = new Mock<IPacketManager>();
        var mapTime = new Mock<IMapTimeTrackerInterface>();
        Func<Action, bool> applyUnderLease = null;
        mapTime
            .Setup(value => value.ApplyAuthorityTimeSample(123456L, It.IsAny<Func<Action, bool>>()))
            .Callback<long, Func<Action, bool>>((_, apply) => applyUnderLease = apply);
        var network = new Mock<INetwork>();
        CampaignTimePacket relayed = default;
        network.Setup(value => value.Send(observerPeer, It.IsAny<IPacket>()))
            .Callback<NetPeer, IPacket>((_, packet) => relayed = (CampaignTimePacket)packet);

        var authorityContext = new CampaignAuthorityContext(identity, new ControllerIdProvider());
        using var handler = new CampaignAuthorityTimePacketRelayHandler(
            packetManager.Object,
            connections.Object,
            catchupQueue.Object,
            players.Object,
            network.Object,
            authorityContext,
            mapTime.Object);

        var sample = new CampaignTimePacket(123456L, -1, lease);
        handler.HandlePacket(authorityPeer, sample);

        mapTime.Verify(
            value => value.ApplyAuthorityTimeSample(123456L, It.IsAny<Func<Action, bool>>()),
            Times.Once);
        Assert.NotNull(applyUnderLease);
        bool applied = false;
        Assert.True(applyUnderLease(() => applied = true));
        Assert.True(applied);
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-paused", string.Empty, 5, isPaused: true)));
        applied = false;
        Assert.False(applyUnderLease(() => applied = true));
        Assert.False(applied);
        Assert.True(relayed.Matches(lease));
        Assert.Equal(123456L, relayed.ServerTicks);
        Assert.Equal(17, relayed.JoinPacketsRemaining);
        network.Verify(value => value.Send(authorityPeer, It.IsAny<IPacket>()), Times.Never);
    }

    [Fact]
    public void StaleLeaseAndNonAuthorityPeerCannotRelayClock()
    {
        var currentLease = new AuthorityLease("room-a", "authority-b", "controller-b", 4);
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(currentLease, "controller-a"));
        var peer = new TestNetwork().CreatePeer();
        var wrongPlayer = new Player("controller-c", "hero-c", "party-c", "clan-c", "character-c");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(peer, out wrongPlayer)).Returns(true);
        var connections = new Mock<IConnectionCollection>();
        connections.Setup(value => value.HasCompletedCampaignSynchronization(peer)).Returns(true);
        var network = new Mock<INetwork>();
        var mapTime = new Mock<IMapTimeTrackerInterface>();

        using var handler = new CampaignAuthorityTimePacketRelayHandler(
            Mock.Of<IPacketManager>(),
            connections.Object,
            Mock.Of<IConnectionMessageQueue>(),
            players.Object,
            network.Object,
            new CampaignAuthorityContext(identity, new ControllerIdProvider()),
            mapTime.Object);

        handler.HandlePacket(peer, new CampaignTimePacket(
            123456L, -1, new AuthorityLease("room-a", "authority-old", "controller-b", 3)));
        handler.HandlePacket(peer, new CampaignTimePacket(123456L, -1, currentLease));

        mapTime.Verify(
            value => value.ApplyAuthorityTimeSample(It.IsAny<long>(), It.IsAny<Func<Action, bool>>()),
            Times.Never);
        network.Verify(value => value.Send(It.IsAny<NetPeer>(), It.IsAny<IPacket>()), Times.Never);
    }
}
