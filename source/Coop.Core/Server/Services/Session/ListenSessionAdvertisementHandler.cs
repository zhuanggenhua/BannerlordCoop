using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Coop.Core.Common.Session.Messages;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Session.Messages;
using Coop.Steam;
using GameInterface.Services.GameDebug.Messages;
using Serilog;
using System;

namespace Coop.Core.Server.Services.Session;

public interface IListenSessionAdvertisementHandler : IDisposable
{
}

/// <summary>Advertises the local player's room after its native actor and UDP listener are ready.</summary>
public sealed class ListenSessionAdvertisementHandler : IListenSessionAdvertisementHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ListenSessionAdvertisementHandler>();
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly ISessionAdvertiser advertiser;
    private readonly ISteamLobbyOwner lobbyOwner;
    private readonly ISessionTunnelHost tunnelHost;
    private readonly ISessionJoinInfoSource joinInfoSource;
    private readonly INetworkConfig networkConfig;
    private bool started;
    private bool disposed;
    private int remotePlayers;

    public ListenSessionAdvertisementHandler(IMessageBroker messageBroker, INetwork network,
        ISessionAdvertiser advertiser, ISteamLobbyOwner lobbyOwner, ISessionTunnelHost tunnelHost,
        ISessionJoinInfoSource joinInfoSource, INetworkConfig networkConfig)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.advertiser = advertiser;
        this.lobbyOwner = lobbyOwner;
        this.tunnelHost = tunnelHost;
        this.joinInfoSource = joinInfoSource;
        this.networkConfig = networkConfig;
        messageBroker.Subscribe<ServerListening>(Handle_ServerListening);
        messageBroker.Subscribe<ConnectedPlayersChanged>(Handle_ConnectedPlayersChanged);
        messageBroker.Subscribe<PlayerConnected>(Handle_PlayerConnected);
        lobbyOwner.LobbyChanged += Handle_LobbyChanged;
    }

    private void Handle_ServerListening(MessagePayload<ServerListening> _)
    {
        GameThread.RunSafe(StartAdvertising, context: "ListenHostAdvertiseSteamSession");
    }

    private void StartAdvertising()
    {
        if (disposed || started) return;
        try
        {
            tunnelHost.Start(networkConfig.Port);
            if (!tunnelHost.IsListening)
                throw new InvalidOperationException("Steam 中继监听未就绪");
            started = true;
            advertiser.Advertise(GetJoinInfo());
            if (started) Logger.Information("Steam 好友房间正在创建，房主 UDP 端口 {Port}", networkConfig.Port);
        }
        catch (Exception ex)
        {
            started = false;
            Logger.Error(ex, "Steam 好友房间启动失败，Direct IP 服务仍可使用");
            messageBroker.Publish(this, new SendInformationMessage("Steam 好友房间启动失败，请导出诊断日志；仍可使用 Direct IP。"));
            StopAdvertising();
        }
    }

    private void Handle_ConnectedPlayersChanged(MessagePayload<ConnectedPlayersChanged> payload)
    {
        int count = Math.Max(0, payload.What.ConnectedPlayers);
        GameThread.RunSafe(() =>
        {
            if (disposed) return;
            remotePlayers = count;
            if (started) advertiser.Advertise(GetJoinInfo());
        }, context: "ListenHostRefreshSteamPlayers");
    }

    private SessionJoinInfo GetJoinInfo()
    {
        var info = joinInfoSource.Get();
        info.ConnectedPlayers = remotePlayers + 1;
        return info;
    }

    private void Handle_PlayerConnected(MessagePayload<PlayerConnected> payload)
    {
        GameThread.RunSafe(() =>
        {
            if (!disposed && lobbyOwner.LobbyId != 0)
                network.Send(payload.What.PlayerPeer, new NetworkSessionLobbyChanged(lobbyOwner.LobbyId));
        }, context: "ListenHostSendSteamLobby");
    }

    private void Handle_LobbyChanged(ulong lobbyId)
    {
        GameThread.RunSafe(() =>
        {
            if (disposed || !started || lobbyId == 0) return;
            network.SendAll(new NetworkSessionLobbyChanged(lobbyId));
            Logger.Information("Steam 好友房间已就绪，房间 {LobbyId}，可通过 Steam 邀请好友", lobbyId.ToString());
            messageBroker.Publish(this, new SendInformationMessage("Steam 好友房间已就绪，可以通过 Steam 好友列表邀请加入。"));
        }, context: "ListenHostPublishSteamLobby");
    }

    private void StopAdvertising()
    {
        try { advertiser.StopAdvertising(); }
        finally { tunnelHost.Stop(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        messageBroker.Unsubscribe<ServerListening>(Handle_ServerListening);
        messageBroker.Unsubscribe<ConnectedPlayersChanged>(Handle_ConnectedPlayersChanged);
        messageBroker.Unsubscribe<PlayerConnected>(Handle_PlayerConnected);
        lobbyOwner.LobbyChanged -= Handle_LobbyChanged;
        GameThread.RunSafe(StopAdvertising, context: "ListenHostStopSteamAdvertising");
    }
}
