using Common;
using GameInterface.Services.Players;
using LiteNetLib;
using System;

namespace GameInterface.Services.CampaignAuthority;

/// <summary>Authenticates host-relayed authority commands against the current room membership.</summary>
internal static class CampaignAuthorityCommandSenderValidator
{
    internal static bool IsRoomHost(NetPeer sender, IPlayerManager players, ISessionIdentity sessionIdentity)
    {
        if (sender == null || players == null || sessionIdentity == null ||
            string.IsNullOrWhiteSpace(sessionIdentity.RoomHostControllerId) ||
            !players.TryGetPlayer(sender, out var player) || player == null ||
            !string.Equals(player.ControllerId, sessionIdentity.RoomHostControllerId, StringComparison.Ordinal) ||
            !players.TryGetPeer(player.ControllerId, out var currentPeer))
            return false;

        return ReferenceEquals(sender, currentPeer);
    }
}
