using Common;
using Common.Tests.Utils;
using Coop.Core.Server;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Instances.Handlers;
using Coop.Tests.Mocks;
using Missions.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Net;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Coop.Tests.Server.Services.Instances;

[Collection(ModInformationRoleCollection.Name)]
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

    [Fact]
    public void LocalEntryRepliesLocallyAndIntroducesTheRegisteredRemoteMember()
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var players = new Mock<IPlayerManager>();
            var localPlayer = new Player("A", "heroA", "partyA", "clanA", "characterA");
            players.Setup(value => value.TryGetPlayer("A", out localPlayer)).Returns(true);
            var controller = new Mock<IControllerIdProvider>();
            controller.SetupGet(value => value.ControllerId).Returns("A");
            var manager = new MissionManager(players.Object, controller.Object);
            var remote = CreatePeer();
            Assert.True(manager.TryEnterMission(remote, "B", "battle", out var remoteEntry));
            var broker = new TestMessageBroker();
            var network = new TestNetwork();
            using var handler = new ServerMissionMembershipHandler(broker, manager, network, players.Object, null, controller.Object);
            broker.Publish(new LocalPlayerEndpoint("A"), new NetworkMissionEntered("ignored", "battle"));
            GameThread.Run(() => { }, blocking: true);
            var credential = Assert.Single(broker.Messages.GetMessages<NetworkMissionCredentialIssued>());
            Assert.NotEqual(Guid.Empty, credential.PeerCredential);
            var introduction = Assert.Single(broker.Messages.GetMessages<NetworkMissionPeerEntered>());
            Assert.Equal("B", introduction.ControllerId);
            Assert.Equal(remoteEntry.PeerCredential, introduction.PeerCredential);
            var remoteIntroduction = Assert.Single(network.GetPeerMessagesFromType<NetworkMissionPeerEntered>(remote));
            Assert.Equal("A", remoteIntroduction.ControllerId);
            Assert.Equal(credential.PeerCredential, remoteIntroduction.PeerCredential);
            Assert.True(manager.TryGetControllers("battle", out var members));
            Assert.Equal(2, members.Count);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void LocalDiscoveryRequiresMembershipAndUsesTheIssuedCredential()
    {
        var manager = CreateManager();
        var endpoint = new LocalPlayerEndpoint("A");
        var request = Guid.NewGuid();
        Assert.False(manager.TryAuthorizeIntroduction(endpoint, "battle", request, out _));
        Assert.True(manager.TryEnterMission(endpoint, "battle", out var entry));
        Assert.True(manager.TryAuthorizeIntroduction(endpoint, "battle", request, out var token));
        Assert.True(manager.TryAuthorizeIntroduction(endpoint, "battle", request, out var repeated));
        Assert.Equal(token, repeated);
        var nat = new NetManager(null);
        var local = new IPEndPoint(IPAddress.Loopback, 52811);
        manager.HandleIntroductionRequest(nat.NatPunchModule, local, local, token);
        Assert.Equal(1, manager.GetDiagnostics().PunchEndpoints);
        manager.HandleIntroductionRequest(nat.NatPunchModule, local, local, token);
        Assert.Equal(1, manager.GetDiagnostics().PunchEndpoints);
        Assert.True(manager.TryLeaveMission(endpoint, "battle", out _));
        Assert.Equal(0, manager.GetDiagnostics().PunchEndpoints);
        manager.HandleIntroductionRequest(nat.NatPunchModule, local, local, token);
        Assert.Equal(0, manager.GetDiagnostics().PunchEndpoints);
    }

    [Fact]
    public void LocalRoomControlDispatchesTheConcreteMessageWithoutSendingToAPeer()
    {
        var broker = new Common.Messaging.MessageBroker();
        var server = new Mock<ICoopServer>(MockBehavior.Strict);
        var controller = new Mock<IControllerIdProvider>();
        controller.SetupGet(value => value.ControllerId).Returns("A");
        object receivedSource = null;
        broker.Subscribe<NetworkMissionEntered>(payload => receivedSource = payload.Who);
        var room = new LocalMissionRoomNetwork(server.Object, broker, controller.Object, CreateManager());
        room.SendAll(new NetworkMissionEntered("A", "battle"));
        Assert.Equal("A", Assert.IsType<LocalPlayerEndpoint>(receivedSource).ControllerId);
        server.VerifyNoOtherCalls();
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
