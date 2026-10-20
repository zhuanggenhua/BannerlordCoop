using Coop.Core.Server.Services.Instances;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Net;
using System.Reflection;
using Xunit;

namespace Coop.Tests.Server.Services.Instances;

public class LocalMissionEndpointTests
{
    [Fact]
    public void LocalMembershipSharesTheInstanceWithoutAPeerOrSecondPlayer()
    {
        var manager = CreateManager();
        var local = new LocalPlayerEndpoint("A");
        Assert.True(manager.TryEnterMission(local, "battle", out var entry));
        Assert.True(entry.IsFirstMember);
        Assert.True(manager.TryEnterMission(local, "battle", out var repeated));
        Assert.Equal(MissionEntryStatus.Unchanged, repeated.Status);
        Assert.Equal(entry.PeerCredential, repeated.PeerCredential);

        var peer = CreatePeer();
        Assert.True(manager.TryEnterMission(peer, "B", "battle", out var remote));
        Assert.False(remote.IsFirstMember);
        var existing = Assert.Single(remote.ExistingEndpoints);
        Assert.IsType<LocalPlayerEndpoint>(existing.endpoint);
        Assert.Null(existing.endpoint.Peer);
        Assert.Equal("A", existing.endpoint.ControllerId);
        Assert.Equal(entry.PeerCredential, existing.peerCredential);
        Assert.Empty(remote.ExistingMembers);

        Assert.True(manager.TryGetControllers("battle", out var controllers));
        Assert.Equal(2, controllers.Count);
        Assert.True(manager.TryLeaveMission(peer, "B", "battle", out var departure));
        Assert.False(departure.IsInstanceEmpty);
        Assert.IsType<LocalPlayerEndpoint>(Assert.Single(departure.RemainingEndpoints));
        Assert.True(manager.TryLeaveMission(local, "battle", out var last));
        Assert.True(last.IsInstanceEmpty);
        Assert.False(manager.IsControllerInMission("A"));
    }

    [Fact]
    public void LocalIdentityMustMatchTheNativeRegisteredPlayer()
    {
        var manager = CreateManager();
        Assert.False(manager.TryEnterMission(new LocalPlayerEndpoint("B"), "battle", out _));
        Assert.False(manager.TryEnterMission(new LocalPlayerEndpoint("Server"), "battle", out _));
        Assert.False(CreateManager(registered: false).TryEnterMission(new LocalPlayerEndpoint("A"), "battle", out _));
    }

    [Fact]
    public void LocalAndRemoteEndpointsCannotReplaceEachOthersIdentity()
    {
        var manager = CreateManager();
        Assert.True(manager.TryEnterMission(new LocalPlayerEndpoint("A"), "battle", out _));
        Assert.False(manager.TryEnterMission(CreatePeer(), "A", "battle", out _));
        Assert.True(manager.IsControllerInMission("A"));
        Assert.False(manager.TryGetRelayTarget(CreatePeer(), "battle", "A", out _));
    }

    [Fact]
    public void LocalMembershipObeysConclusionFenceAndMovesAtomically()
    {
        var manager = CreateManager();
        var endpoint = new LocalPlayerEndpoint("A");
        Assert.True(manager.TryEnterMission(endpoint, "first", out _));
        Assert.True(manager.TryEnterMission(endpoint, "second", out var entry));
        Assert.True(Assert.Single(entry.PreviousDepartures).IsInstanceEmpty);
        Assert.True(manager.TryBeginActiveInstanceConclusion("second", new[] { "A" }));
        Assert.False(manager.TryEnterMission(endpoint, "second", out _));
        Assert.True(manager.CompleteInstanceConclusion("second", succeeded: false));
        Assert.True(manager.TryLeaveMission(endpoint, "second", out _));
    }

    private static MissionManager CreateManager(bool registered = true)
    {
        var players = new Mock<IPlayerManager>();
        var player = new Player("A", "heroA", "partyA", "clanA", "characterA");
        players.Setup(value => value.TryGetPlayer("A", out player)).Returns(registered);
        var controller = new Mock<IControllerIdProvider>();
        controller.SetupGet(value => value.ControllerId).Returns("A");
        return new MissionManager(players.Object, controller.Object);
    }

    private static NetPeer CreatePeer() => (NetPeer)typeof(NetPeer).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(NetManager), typeof(IPEndPoint), typeof(int) }, null)!.Invoke(
            new object[] { new NetManager(null), new IPEndPoint(IPAddress.Loopback, 52810), 1 });
}
