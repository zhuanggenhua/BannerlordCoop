using Common.Logging;
using Common.Messaging;
using Common.Network.Session;
using Coop.Steam;
using GameInterface.Services.GameDebug.Messages;
using Serilog;

namespace Coop.Core.Server.Services.Session;

/// <summary>Reports the original advertiser's asynchronous failure through the local player's UI.</summary>
public sealed class ListenSteamLobbyAdvertiser : SteamLobbyAdvertiser
{
    private static readonly ILogger Logger = LogManager.GetLogger<ListenSteamLobbyAdvertiser>();
    private readonly IMessageBroker messageBroker;
    private bool disposed;

    public ListenSteamLobbyAdvertiser(ISteamLobbyApi lobbyApi, IMessageBroker messageBroker) : base(lobbyApi)
    {
        this.messageBroker = messageBroker;
    }

    protected override void OnLobbyUnavailable(SessionJoinInfo info)
    {
        if (disposed) return;
        Logger.Error("Steam 好友房间创建或更新失败，请查看此前的 Steam 原始错误日志");
        messageBroker.Publish(this, new SendInformationMessage("Steam 好友房间创建或更新失败，请导出诊断日志；仍可使用 Direct IP。"));
    }

    public override void Dispose()
    {
        disposed = true;
        base.Dispose();
    }
}
