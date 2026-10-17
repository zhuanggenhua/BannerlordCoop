using LiteNetLib;

namespace GameInterface.Services.Players;

public static class PlayerManagerExtensions
{
    public static bool ValidateCurrentPartySender(this IPlayerManager players, object sender, string partyId)
    {
        return !string.IsNullOrEmpty(partyId) && sender is NetPeer peer &&
            players.TryGetPlayer(peer, out var player) && player != null &&
            !players.IsReservedController(player.ControllerId) && player.MobilePartyId == partyId &&
            players.TryGetPeer(player.ControllerId, out var currentPeer) && ReferenceEquals(currentPeer, peer);
    }
    public static bool ValidateHeroSender(this IPlayerManager playerManager, object sender, string requestedHeroId)
    {
        return !string.IsNullOrEmpty(requestedHeroId) &&
            sender is NetPeer peer &&
            playerManager.TryGetPlayer(peer, out var player) &&
            player?.HeroId == requestedHeroId;
    }
}
