using Common.Logging;
using Common.Network;
using Common.PacketHandlers;
using GameInterface.Services.Missions;
using LiteNetLib;
using Serilog;

namespace Coop.Core.Server.Services.Instances.Relay;

/// <summary>
/// Server-side relay fallback: forwards a <see cref="RelayPacket"/>'s payload to the live connections of
/// the controllers it names, resolved per instance by the <see cref="IMissionManager"/>. The payload is
/// relayed verbatim to remote targets. A Listen local target uses a separately verified local receiver.
/// </summary>
internal class RelayPacketHandler : IPacketHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<RelayPacketHandler>();
    private readonly IPacketManager packetManager;
    private readonly IMissionManager missionManager;
    private readonly ILocalMissionRelayReceiver localReceiver;
    private readonly IMissionRelaySourceVerifier sourceVerifier;

    public PacketType PacketType => PacketType.Relay;

    public RelayPacketHandler(IPacketManager packetManager, IMissionManager missionManager,
        ILocalMissionRelayReceiver localReceiver = null, IMissionRelaySourceVerifier sourceVerifier = null)
    {
        this.packetManager = packetManager;
        this.missionManager = missionManager;
        this.localReceiver = localReceiver;
        this.sourceVerifier = sourceVerifier;

        packetManager.RegisterPacketHandler(this);
    }

    public void Dispose()
    {
        packetManager.RemovePacketHandler(this);
    }

    public void HandlePacket(NetPeer peer, IPacket packet)
    {
        RelayPacket relayPacket = (RelayPacket)packet;

        if (!missionManager.TryGetRelayTarget(peer, relayPacket.InstanceId, relayPacket.ControllerId, out var target))
        {
            if (localReceiver != null && sourceVerifier != null &&
                sourceVerifier.TryCaptureLocalDelivery(peer, relayPacket.InstanceId, relayPacket.ControllerId, out var receipt))
            {
                if (!localReceiver.TryReceive(receipt, relayPacket.Payload))
                    Logger.Warning("Local Mission relay was refused for instance {Instance} from {Controller}", receipt.InstanceId, receipt.ControllerId);
                return;
            }
            Logger.Error("Failed to get peer for instance ({InstanceId}) controller ({ControllerId})", relayPacket.InstanceId, relayPacket.ControllerId);
            return;
        }

        target.Send(relayPacket.Payload, relayPacket.DeliveryMethod);
    }
}
