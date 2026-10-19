using Common;
using Common.Network;
using GameInterface.Services.Players;
using LiteNetLib;
using System;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>Authenticates host-relayed authority commands against the current room membership.</summary>
internal static class CampaignAuthorityCommandSenderValidator
{
    internal static bool IsRoomHost(NetPeer sender, IPlayerManager players, ISessionIdentity sessionIdentity,
        INetwork network)
    {
        if (sender == null || players == null || sessionIdentity == null ||
            string.IsNullOrWhiteSpace(sessionIdentity.RoomHostControllerId))
            return false;

        // Client registrations describe actors, not connections. Only its live transport owns
        // the host connection; never infer it from an endpoint or the first command received.
        if (ModInformation.IsClient)
            return players.TryGetPlayer(sessionIdentity.RoomHostControllerId, out var host) && host != null &&
                string.Equals(host.ControllerId, sessionIdentity.RoomHostControllerId, StringComparison.Ordinal) &&
                network is IRoomHostPeerProvider provider && provider.IsCurrentRoomHostPeer(sender);

        if (!players.TryGetPlayer(sender, out var player) || player == null ||
            !string.Equals(player.ControllerId, sessionIdentity.RoomHostControllerId, StringComparison.Ordinal) ||
            !players.TryGetPeer(player.ControllerId, out var currentPeer))
            return false;

        return ReferenceEquals(sender, currentPeer);
    }
}
