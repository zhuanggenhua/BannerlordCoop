using Common.Network;
using Common.PacketHandlers;
using Coop.Core.Common.Network.Packets;
using Coop.Core.Server.Connections;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;

namespace Coop.Core.Server.Services.Time.Handlers;

/// <summary>Relays sequenced campaign-time samples from the current remote authority.</summary>
public sealed class CampaignAuthorityTimePacketRelayHandler : IPacketHandler
{
    public PacketType PacketType => PacketType.CampaignTime;

    private readonly IPacketManager packetManager;
    private readonly IConnectionCollection connections;
    private readonly IConnectionMessageQueue connectionMessageQueue;
    private readonly IPlayerManager players;
    private readonly INetwork network;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IMapTimeTrackerInterface mapTimeTracker;

    public CampaignAuthorityTimePacketRelayHandler(
        IPacketManager packetManager,
        IConnectionCollection connections,
        IConnectionMessageQueue connectionMessageQueue,
        IPlayerManager players,
        INetwork network,
        ICampaignAuthorityContext authorityContext,
        IMapTimeTrackerInterface mapTimeTracker)
    {
        this.packetManager = packetManager;
        this.connections = connections;
        this.connectionMessageQueue = connectionMessageQueue;
        this.players = players;
        this.network = network;
        this.authorityContext = authorityContext;
        this.mapTimeTracker = mapTimeTracker;
        packetManager.RegisterPacketHandler(this);
    }

    public void Dispose() => packetManager.RemovePacketHandler(this);

    public void HandlePacket(NetPeer peer, IPacket packet)
    {
        if (peer == null || packet is not CampaignTimePacket sample ||
            !authorityContext.TryGetCurrentLease(out var lease) || !sample.Matches(lease) ||
            !connections.HasCompletedCampaignSynchronization(peer) ||
            !players.TryGetPlayer(peer, out var player) || player == null ||
            player.ControllerId != lease.AuthorityControllerId ||
            !players.TryGetPeer(player.ControllerId, out var currentPeer) || currentPeer != peer)
            return;

        mapTimeTracker.ApplyAuthorityTimeSample(
            sample.ServerTicks,
            () => authorityContext.TryGetCurrentLease(out var currentLease) &&
                currentLease == lease && sample.Matches(currentLease));

        foreach (var connection in connections)
        {
            if (ReferenceEquals(connection.Peer, peer)) continue;

            int joinPacketsRemaining = connectionMessageQueue.TryGetCatchUpPacketsRemaining(
                connection.Peer,
                out int packetsRemaining)
                ? packetsRemaining
                : -1;

            network.Send(
                connection.Peer,
                new CampaignTimePacket(sample.ServerTicks, joinPacketsRemaining, lease));
        }
    }
}
