using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.TroopRosters.Messages;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using Serilog;
using System.Collections.Generic;
using System.Threading;

using Common;

namespace Coop.Core.Client.Services.TroopRosters.Handlers;
public class ClientTroopRosterHandler : IHandler
{
    private readonly ILogger Logger = LogManager.GetLogger<ClientTroopRosterHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IPlayerManager players;
    private readonly ITroopRosterInterface troopRosterInterface;
    private readonly object commandGate = new object();
    private long nextRequestSequence;
    private AuthorityLease lastAuthorityCommandLease;
    private long lastAuthorityCommandSequence;

    public ClientTroopRosterHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        ICampaignAuthorityContext authorityContext,
        IPlayerManager players,
        ITroopRosterInterface troopRosterInterface)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.authorityContext = authorityContext;
        this.players = players;
        this.troopRosterInterface = troopRosterInterface;

        messageBroker.Subscribe<RecruitmentAttempted>(HandleOnRecruitmentDone);
        messageBroker.Subscribe<NetworkCampaignAuthorityRecruitmentCommand>(HandleAuthorityRecruitmentCommand);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<RecruitmentAttempted>(HandleOnRecruitmentDone);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityRecruitmentCommand>(HandleAuthorityRecruitmentCommand);
    }

    private void HandleOnRecruitmentDone(MessagePayload<RecruitmentAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MobileParty, out var mobilePartyId)) return;

        List<TroopInfo> troops = new();
        foreach (var (hero, character, index) in obj.TroopsInCart)
        {
            if (!objectManager.TryGetIdWithLogging(hero, out var heroId)) continue;
            if (!objectManager.TryGetIdWithLogging(character, out var characterId)) continue;

            troops.Add(new TroopInfo(heroId, characterId, index));
        }

        if (troops.Count <= 0)
        {
            Logger.Warning("No troops in cart");
            return;
        }

        AuthorityLease lease = default;
        if (authorityContext.IsAuthorityKnown &&
            (!authorityContext.TryGetCurrentLease(out lease) || !lease.IsValid || lease.IsPaused))
        {
            Logger.Warning("Dropped recruitment request for party {MobilePartyId} because the campaign lease is paused or unavailable",
                mobilePartyId);
            return;
        }

        long sequence = Interlocked.Increment(ref nextRequestSequence);
        var message = new ClientRequestRecruitment(mobilePartyId, troops.ToArray(), sequence, lease);

        network.SendAll(message);
    }

    internal void HandleAuthorityRecruitmentCommand(
        MessagePayload<NetworkCampaignAuthorityRecruitmentCommand> payload)
    {
        var command = payload.What;
        if (!authorityContext.TryGetLocalAuthorityLease(out var lease) ||
            !command.Matches(lease) ||
            !players.TryGetPlayer(command.OriginControllerId, out Player player) || player == null ||
            player.MobilePartyId != command.MobilePartyId ||
            !TryAdvanceAuthorityCommandSequence(lease, command.Sequence))
            return;

        GameThread.RunSafe(() =>
        {
            // Ownership may change while the command waits for the game thread.
            if (!authorityContext.TryGetLocalAuthorityLease(out var currentLease) || currentLease != lease ||
                !players.TryGetPlayer(command.OriginControllerId, out Player currentPlayer) ||
                currentPlayer == null || currentPlayer.MobilePartyId != command.MobilePartyId)
                return;

            try
            {
                troopRosterInterface.HandleOnRecruitmentDone(command.MobilePartyId, command.TroopsInCart);
            }
            catch (System.Exception e)
            {
                Logger.Error(e, "Failed to apply authority command {Message}", nameof(NetworkCampaignAuthorityRecruitmentCommand));
            }
        }, context: nameof(ClientTroopRosterHandler));
    }

    private bool TryAdvanceAuthorityCommandSequence(AuthorityLease lease, long sequence)
    {
        lock (commandGate)
        {
            if (lastAuthorityCommandLease != lease)
            {
                lastAuthorityCommandLease = lease;
                lastAuthorityCommandSequence = 0;
            }

            if (sequence <= lastAuthorityCommandSequence) return false;
            lastAuthorityCommandSequence = sequence;
            return true;
        }
    }
}
