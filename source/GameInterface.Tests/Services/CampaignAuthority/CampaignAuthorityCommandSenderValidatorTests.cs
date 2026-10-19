using Common;
using Common.Network;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityCommandSenderValidatorTests : IDisposable
{
    private readonly SessionMode previousMode = ModInformation.SessionMode;
    static CampaignAuthorityCommandSenderValidatorTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void ServerRequiresCurrentPeerForRoomHostController()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        using var peers = new TestNetwork();
        var sender = peers.CreatePeer();
        var host = Host();
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(sender, out host)).Returns(true);
        players.Setup(value => value.TryGetPeer("controller-host", out sender)).Returns(true);
        Assert.True(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), peers));
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(peers.CreatePeer(), players.Object, Identity(), peers));
        var replacement = peers.CreatePeer();
        players.Setup(value => value.TryGetPeer("controller-host", out replacement)).Returns(true);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), peers));
    }

    [Fact]
    public void ServerRejectsOtherControllerEvenIfTransportClaimsHost()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        using var peers = new TestNetwork();
        var sender = peers.CreatePeer();
        var other = new Player("controller-other", "", "", "", "");
        var players = new Mock<IPlayerManager>();
        players.Setup(value => value.TryGetPlayer(sender, out other)).Returns(true);
        var network = Transport(sender);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), network.Object));
    }

    [Fact]
    public void ClientUsesTransportWithRegisteredHostAndNoPlayerPeerMapping()
    {
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        using var peers = new TestNetwork();
        var sender = peers.CreatePeer();
        var players = ClientPlayers();
        var network = Transport(sender);
        Assert.True(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), network.Object));
        players.Verify(value => value.TryGetPlayer(It.IsAny<NetPeer>(), out It.Ref<Player>.IsAny), Times.Never);
        players.Verify(value => value.TryGetPeer(It.IsAny<string>(), out It.Ref<NetPeer>.IsAny), Times.Never);
    }

    [Fact]
    public void ClientRejectsUnknownNetworkEvenIfServerStylePlayerMappingExists()
    {
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        using var peers = new TestNetwork();
        var sender = peers.CreatePeer();
        var host = Host();
        var players = ClientPlayers();
        players.Setup(value => value.TryGetPlayer(sender, out host)).Returns(true);
        players.Setup(value => value.TryGetPeer("controller-host", out sender)).Returns(true);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), peers));
    }

    [Fact]
    public void ClientRejectsSupersededPeerAndAcceptsReplacement()
    {
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        using var peers = new TestNetwork();
        var old = peers.CreatePeer();
        var current = peers.CreatePeer();
        var network = Transport(current);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(old, ClientPlayers().Object, Identity(), network.Object));
        Assert.True(CampaignAuthorityCommandSenderValidator.IsRoomHost(current, ClientPlayers().Object, Identity(), network.Object));
    }

    [Fact]
    public void ClientRequiresHostIdentityEvenForCurrentTransport()
    {
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        using var peers = new TestNetwork();
        var sender = peers.CreatePeer();
        var network = Transport(sender);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, Mock.Of<IPlayerManager>(), Identity(), network.Object));
        var players = ClientPlayers();
        var impostor = new Player("controller-other", "", "", "", "");
        players.Setup(value => value.TryGetPlayer("controller-host", out impostor)).Returns(true);
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, Identity(), network.Object));
        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, ClientPlayers().Object, Mock.Of<ISessionIdentity>(), network.Object));
    }

    private static Player Host() => new("controller-host", "hero-host", "party-host", "clan-host", "character-host");
    private static Mock<IPlayerManager> ClientPlayers()
    {
        var players = new Mock<IPlayerManager>();
        var host = Host();
        players.Setup(value => value.TryGetPlayer("controller-host", out host)).Returns(true);
        return players;
    }
    private static ISessionIdentity Identity() => Mock.Of<ISessionIdentity>(value => value.RoomHostControllerId == "controller-host");
    private static Mock<INetwork> Transport(NetPeer current)
    {
        var network = new Mock<INetwork>();
        network.As<IRoomHostPeerProvider>().Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
            .Returns<NetPeer>(peer => ReferenceEquals(current, peer));
        return network;
    }
    public void Dispose() => ModInformation.SessionMode = previousMode;
}
