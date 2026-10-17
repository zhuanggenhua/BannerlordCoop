using Common;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Moq;
using Serilog;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public class ListenAuthorityPlayerTests : IDisposable
{
    private readonly SessionMode previous = ModInformation.SessionMode;
    public void Dispose() => ModInformation.SessionMode = previous;

    [Fact]
    public void LocalPresenceRequiresRegisteredActorButNoLoopbackPeer()
    {
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        var controller = new ControllerIdProvider();
        controller.SetControllerId("LocalA");
        var players = new PlayerManager(Mock.Of<ILogger>(), Mock.Of<IObjectManager>(), controller);
        var local = new Player("LocalA", "", "", "", "");
        var remote = new Player("RemoteB", "", "", "", "");
        Assert.False(players.IsConnected(local));
        Assert.True(players.AddPlayer(local));
        Assert.True(players.AddPlayer(remote));
        Assert.True(players.IsConnected(local));
        Assert.False(players.TryGetPeer("LocalA", out _));
        Assert.False(players.IsConnected(remote));
        Assert.False(players.AddPlayer(local));
        Assert.Equal(2, players.Players.Count);
        Assert.True(players.RemovePlayer(local));
        Assert.False(players.IsConnected(local));
    }

    [Fact]
    public void DedicatedControllerIsNotConsideredAnOnlineLocalPlayer()
    {
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        var controller = new ControllerIdProvider();
        controller.SetControllerId("Server");
        var players = new PlayerManager(Mock.Of<ILogger>(), Mock.Of<IObjectManager>(), controller);
        var player = new Player("Server", "", "", "", "");
        players.AddPlayer(player);
        Assert.False(players.IsConnected(player));
    }
}
