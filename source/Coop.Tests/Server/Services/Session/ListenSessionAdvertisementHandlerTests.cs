using Common;
using Common.Network;
using Common.Network.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Session;
using Coop.Core.Common.Configuration;
using Coop.Core.Common.Session.Messages;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Session;
using Coop.Core.Server.Services.Session.Messages;
using Coop.Steam;
using GameInterface.Services.GameDebug.Messages;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

public class ListenSessionAdvertisementHandlerTests : IDisposable
{
    private readonly TestMessageBroker broker = new();
    private readonly Mock<INetwork> network = new();
    private readonly Mock<ISessionAdvertiser> advertiser = new();
    private readonly Mock<ISteamLobbyOwner> owner = new();
    private readonly Mock<ISessionTunnelHost> tunnel = new();
    private readonly List<SessionJoinInfo> advertisements = new();
    private readonly ListenSessionAdvertisementHandler handler;

    public ListenSessionAdvertisementHandlerTests()
    {
        tunnel.SetupGet(value => value.IsListening).Returns(true);
        advertiser.Setup(value => value.Advertise(It.IsAny<SessionJoinInfo>()))
            .Callback<SessionJoinInfo>(info => advertisements.Add(info));
        var config = new NetworkConfig { Port = 4200, Token = "private" };
        handler = new ListenSessionAdvertisementHandler(broker, network.Object, advertiser.Object,
            owner.Object, tunnel.Object, new ConfiguredSessionJoinInfoSource(config), config);
    }

    public void Dispose() => GameThread.Run(handler.Dispose, blocking: true);

    private void Publish<T>(T message) where T : Common.Messaging.IMessage =>
        GameThread.Run(() => broker.Publish(this, message), blocking: true);

    [Fact]
    public void ClientConnectionDoesNotAdvertiseBeforeRoomUdpBinding()
    {
        Publish(new NetworkConnected());
        Publish(new ConnectedPlayersChanged(1));
        Assert.Empty(advertisements);
        tunnel.Verify(value => value.Start(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ListeningStartsTunnelBeforeOneFriendsAdvertisementWithoutGameServerLogin()
    {
        var calls = new List<string>();
        tunnel.Setup(value => value.Start(4200)).Callback(() => calls.Add("tunnel"));
        advertiser.Setup(value => value.Advertise(It.IsAny<SessionJoinInfo>()))
            .Callback<SessionJoinInfo>(info =>
            {
                calls.Add("advertise");
                advertisements.Add(info);
            });
        Publish(new ServerListening());
        Publish(new ServerListening());
        tunnel.Verify(value => value.Start(4200), Times.Once);
        Assert.Equal(new[] { "tunnel", "advertise" }, calls);
        var info = Assert.Single(advertisements);
        Assert.Equal(1, info.ConnectedPlayers);
        Assert.Equal(SessionJoinInfo.CurrentVersion, info.Version);
        Assert.Equal(0UL, info.ServerSteamId);
        Assert.True(info.PasswordRequired);
        Assert.Null(info.Password);
        Assert.Equal(4200, info.Port);
    }

    [Fact]
    public void RemoteCountUpdatesAlwaysIncludeTheLocalActorExactlyOnce()
    {
        Publish(new ConnectedPlayersChanged(2));
        Publish(new ServerListening());
        Publish(new ConnectedPlayersChanged(1));
        Publish(new ConnectedPlayersChanged(0));
        Publish(new ConnectedPlayersChanged(-3));
        Assert.Equal(new[] { 3, 2, 1, 1 }, advertisements.Select(info => info.ConnectedPlayers));
    }

    [Fact]
    public void CanonicalLobbyIsBroadcastAndSentToLaterRealPeer()
    {
        Publish(new ServerListening());
        owner.SetupGet(value => value.LobbyId).Returns(991UL);
        GameThread.Run(() => owner.Raise(value => value.LobbyChanged += null, 991UL), blocking: true);
        Publish(new PlayerConnected(null));
        network.Verify(value => value.SendAll(It.Is<NetworkSessionLobbyChanged>(info => info.LobbyId == 991UL)), Times.Once);
        network.Verify(value => value.Send(It.IsAny<NetPeer>(), It.Is<NetworkSessionLobbyChanged>(info => info.LobbyId == 991UL)), Times.Once);
        Assert.Contains(broker.GetMessagesFromType<SendInformationMessage>(), info => info.Text.Contains("Steam 好友房间已就绪"));
    }

    [Fact]
    public void LobbyNotReadyIsNeverSentToJoiningPeer()
    {
        Publish(new ServerListening());
        Publish(new PlayerConnected(null));
        GameThread.Run(() => owner.Raise(value => value.LobbyChanged += null, 0UL), blocking: true);
        network.Verify(value => value.SendAll(It.IsAny<NetworkSessionLobbyChanged>()), Times.Never);
        network.Verify(value => value.Send(It.IsAny<NetPeer>(), It.IsAny<NetworkSessionLobbyChanged>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnavailableTunnelNeverAdvertisesAndReportsChineseFailure(bool throws)
    {
        if (throws) tunnel.Setup(value => value.Start(4200)).Throws(new InvalidOperationException("original failure"));
        else tunnel.SetupGet(value => value.IsListening).Returns(false);
        Publish(new ServerListening());
        Assert.Empty(advertisements);
        Assert.Contains(broker.GetMessagesFromType<SendInformationMessage>(), info => info.Text.Contains("Steam 好友房间启动失败"));
        tunnel.Verify(value => value.Stop(), Times.Once);
    }

    [Fact]
    public void DisposedHandlerClosesRoomAndCannotRestartOrPublishStaleLobby()
    {
        Publish(new ServerListening());
        GameThread.Run(handler.Dispose, blocking: true);
        Publish(new ServerListening());
        Publish(new ConnectedPlayersChanged(1));
        Publish(new PlayerConnected(null));
        GameThread.Run(() => owner.Raise(value => value.LobbyChanged += null, 99UL), blocking: true);
        tunnel.Verify(value => value.Start(4200), Times.Once);
        tunnel.Verify(value => value.Stop(), Times.Once);
        advertiser.Verify(value => value.StopAdvertising(), Times.Once);
        Assert.Single(advertisements);
        network.Verify(value => value.SendAll(It.IsAny<NetworkSessionLobbyChanged>()), Times.Never);
        Assert.Equal(0, broker.GetTotalSubscribers());
    }

    [Fact]
    public void QueuedListeningAfterDisposalCannotStartTheTunnel()
    {
        GameThread.Run(() =>
        {
            var poller = new Thread(() => broker.Publish(this, new ServerListening()));
            poller.Start();
            poller.Join();
            handler.Dispose();
        }, blocking: true);
        GameThread.Run(() => { }, blocking: true);
        tunnel.Verify(value => value.Start(It.IsAny<int>()), Times.Never);
        Assert.Empty(advertisements);
    }

    [Fact]
    public void AdvertiserExceptionStopsTunnelAndDoesNotClaimReady()
    {
        advertiser.Setup(value => value.Advertise(It.IsAny<SessionJoinInfo>())).Throws(new InvalidOperationException("original failure"));
        Publish(new ServerListening());
        tunnel.Verify(value => value.Stop(), Times.Once);
        Assert.DoesNotContain(broker.GetMessagesFromType<SendInformationMessage>(), info => info.Text.Contains("已就绪"));
    }
}
