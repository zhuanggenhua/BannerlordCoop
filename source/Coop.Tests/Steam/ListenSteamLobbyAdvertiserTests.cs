using Common.Network.Session;
using Common;
using Common.Network;
using Common.Tests.Utils;
using Coop.Core.Server.Services.Session;
using Coop.Core.Common.Configuration;
using Coop.Core.Client.Services.Session;
using Coop.Core.Server.Services.Session.Messages;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Common.Session.Messages;
using GameInterface.Services.GameDebug.Messages;
using Moq;
using Xunit;

namespace Coop.Tests.Steam;

public class ListenSteamLobbyAdvertiserTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeferredCallbackAfterDisposalDoesNotAnnounceOrReportStaleSession(bool success)
    {
        var api = new FakeSteamLobbyApi { CompleteOperationsImmediately = false, CreateSucceeds = success };
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var tunnel = new Mock<ISessionTunnelHost>();
        tunnel.SetupGet(value => value.IsListening).Returns(true);
        var config = new NetworkConfig { Port = 4200 };
        using var advertiser = new ListenSteamLobbyAdvertiser(api, broker);
        using var handler = new ListenSessionAdvertisementHandler(broker, network.Object, advertiser,
            advertiser, tunnel.Object, new ConfiguredSessionJoinInfoSource(config), config);
        GameThread.Run(() =>
        {
            broker.Publish(this, new ServerListening());
            handler.Dispose();
            advertiser.Dispose();
            api.CompletePendingCreate();
        }, blocking: true);
        Assert.False(advertiser.IsAdvertising);
        Assert.Empty(broker.GetMessagesFromType<SendInformationMessage>());
        network.Verify(value => value.SendAll(It.IsAny<NetworkSessionLobbyChanged>()), Times.Never);
        if (success) Assert.Contains(api.NextCreatedLobbyId, api.LeftLobbies);
    }

    [Fact]
    public void MetadataFailureWithdrawsLobbyAndReportsChineseErrorWithoutDisconnectingGameTunnel()
    {
        var api = new FakeSteamLobbyApi();
        var broker = new TestMessageBroker();
        var network = new Mock<INetwork>();
        var tunnel = new Mock<ISessionTunnelHost>();
        tunnel.SetupGet(value => value.IsListening).Returns(true);
        var config = new NetworkConfig { Port = 4200 };
        using var advertiser = new ListenSteamLobbyAdvertiser(api, broker);
        using var handler = new ListenSessionAdvertisementHandler(broker, network.Object, advertiser,
            advertiser, tunnel.Object, new ConfiguredSessionJoinInfoSource(config), config);
        GameThread.Run(() =>
        {
            broker.Publish(this, new ServerListening());
            api.SetLobbyDataSucceeds = false;
            broker.Publish(this, new ConnectedPlayersChanged(1));
        }, blocking: true);
        Assert.False(advertiser.IsAdvertising);
        Assert.Contains(api.NextCreatedLobbyId, api.LeftLobbies);
        Assert.Contains(broker.GetMessagesFromType<SendInformationMessage>(), message => message.Text.Contains("创建或更新失败"));
        tunnel.Verify(value => value.Stop(), Times.Never);
        Assert.Single(broker.GetMessagesFromType<SendInformationMessage>(), message => message.Text.Contains("已就绪"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedOriginalCreateOrMetadataReportsChineseFailure(bool createFails)
    {
        var api = new FakeSteamLobbyApi
        {
            CreateSucceeds = !createFails,
            SetLobbyDataSucceeds = createFails,
        };
        var broker = new TestMessageBroker();
        using var advertiser = new ListenSteamLobbyAdvertiser(api, broker);
        advertiser.Advertise(new SessionJoinInfo { Port = 4200 });
        Assert.False(advertiser.IsAdvertising);
        Assert.Contains(broker.GetMessagesFromType<SendInformationMessage>(), message => message.Text.Contains("Steam 好友房间创建或更新失败"));
    }

    [Fact]
    public void OriginalFriendLobbyRichPresenceAndInviteFlowArePreserved()
    {
        var api = new FakeSteamLobbyApi();
        using var advertiser = new ListenSteamLobbyAdvertiser(api, new TestMessageBroker());
        advertiser.Advertise(new SessionJoinInfo { Port = 4200, ConnectedPlayers = 1 });
        Assert.True(advertiser.IsAdvertising);
        Assert.False(api.LastCreateWasPublic);
        Assert.Contains($"+connect_lobby {api.NextCreatedLobbyId}", api.RichPresenceConnects);
        Assert.True(advertiser.InviteFriends());
        Assert.Contains(api.NextCreatedLobbyId, api.InviteDialogsOpened);
    }
}
