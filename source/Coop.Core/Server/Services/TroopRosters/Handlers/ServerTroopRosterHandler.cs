using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.TroopRosters.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.UI.Notifications.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Entity;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace Coop.Core.Server.Services.TroopRosters.Handlers;
internal class ServerTroopRosterHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerTroopRosterHandler>();
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ITroopRosterInterface troopRosterInterface;
    private readonly IPlayerManager players;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IControllerIdProvider controller;
    private readonly object sequenceGate = new object();
    private readonly Dictionary<NetPeer, long> lastRequestSequenceByPeer = new Dictionary<NetPeer, long>();
    private AuthorityLease authorityCommandLease;
    private long nextAuthorityCommandSequence;

    public ServerTroopRosterHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        ITroopRosterInterface troopRosterInterface,
        IPlayerManager players,
        ICampaignAuthorityContext authorityContext = null,
        IControllerIdProvider controller = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.troopRosterInterface = troopRosterInterface;
        this.players = players;
        this.authorityContext = authorityContext;
        this.controller = controller;

        messageBroker.Subscribe<ClientRequestRecruitment>(HandleOnRecruitmentDone);
        messageBroker.Subscribe<RecruitmentAttempted>(HandleLocalRecruitment);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<ClientRequestRecruitment>(HandleOnRecruitmentDone);
        messageBroker.Unsubscribe<RecruitmentAttempted>(HandleLocalRecruitment);
        lock (sequenceGate)
        {
            lastRequestSequenceByPeer.Clear();
            authorityCommandLease = default;
            nextAuthorityCommandSequence = 0;
        }
    }

    internal void HandleOnRecruitmentDone(MessagePayload<ClientRequestRecruitment> payload)
    {
        var data = payload.What;

        GameThread.RunSafe(() =>
        {
            try
            {
                if (!TryGetRemotePlayer(payload.Who, data.MobilePartyId, out var player)) return;

                if (!TryGetRequestLease(data, out var lease)) return;
                if (payload.Who is NetPeer requestPeer && !TryAdvanceRequestSequence(requestPeer, data.RequestSequence))
                    return;

                if (authorityContext?.IsAuthorityKnown == true)
                {
                    bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var localLease) &&
                        localLease == lease;
                    if (localAuthority)
                    {
                        authorityContext.TryExecuteWithCurrentLease(lease, () =>
                            troopRosterInterface.HandleOnRecruitmentDone(data.MobilePartyId, data.TroopsInCart));
                        return;
                    }

                    if (lease.IsValid && !lease.IsPaused)
                        RouteToRemoteAuthority(data, player.ControllerId, lease);
                    return;
                }

                troopRosterInterface.HandleOnRecruitmentDone(data.MobilePartyId, data.TroopsInCart);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(ClientRequestRecruitment));
            }
        }, context: nameof(ServerTroopRosterHandler));
    }

    internal void HandleLocalRecruitment(MessagePayload<RecruitmentAttempted> payload)
    {
        // This is a local event, never a fabricated NetPeer or a loopback ClientRequestRecruitment.
        var attempt = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!ModInformation.IsListenAuthority || controller == null ||
                attempt.MobileParty == null ||
                !players.TryGetPlayer(controller.ControllerId, out var player) || player == null ||
                !objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var registeredParty) ||
                !ReferenceEquals(attempt.MobileParty, registeredParty) ||
                authorityContext?.IsAuthorityKnown != true ||
                !authorityContext.TryGetCurrentLease(out var lease) || !lease.IsValid || lease.IsPaused ||
                attempt.ObservedLease != lease)
                return;

            // A local writer already ran native OnDone. Only an observing room host publishes a request.
            // The observed lease above rejects a request overtaken by promotion instead of replaying it.
            var troops = new List<TroopInfo>();
            foreach (var (hero, character, index) in attempt.TroopsInCart)
            {
                if (!objectManager.TryGetIdWithLogging(hero, out var heroId) ||
                    !objectManager.TryGetIdWithLogging(character, out var characterId)) return;
                troops.Add(new TroopInfo(heroId, characterId, index));
            }
            if (troops.Count == 0) return;

            if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
            {
                authorityContext.TryExecuteWithCurrentLease(lease, () =>
                    troopRosterInterface.HandleOnRecruitmentDone(player.MobilePartyId, troops.ToArray()));
                return;
            }

            RouteToRemoteAuthority(new ClientRequestRecruitment(player.MobilePartyId, troops.ToArray()),
                player.ControllerId, lease);
        }, context: nameof(ServerTroopRosterHandler));
    }

    private bool TryGetRemotePlayer(object sender, string partyId, out GameInterface.Services.Players.Data.Player player)
    {
        player = null;
        return !string.IsNullOrEmpty(partyId) && sender is NetPeer peer &&
            players.TryGetPlayer(peer, out player) && player != null &&
            !players.IsReservedController(player.ControllerId) &&
            Compact(player.MobilePartyId, typeof(MobileParty)) == Compact(partyId, typeof(MobileParty)) &&
            players.TryGetPeer(player.ControllerId, out var currentPeer) && ReferenceEquals(peer, currentPeer);
    }

    internal bool TryGetRequestLease(ClientRequestRecruitment request, out AuthorityLease lease)
    {
        if (authorityContext?.IsAuthorityKnown != true)
        {
            lease = default;
            return true;
        }

        return authorityContext.TryGetCurrentLease(out lease) && request.MatchesLease(lease);
    }

    internal bool TryAdvanceRequestSequence(NetPeer peer, long sequence)
    {
        if (sequence <= 0) return authorityContext?.IsAuthorityKnown != true;

        lock (sequenceGate)
        {
            if (lastRequestSequenceByPeer.TryGetValue(peer, out var lastSequence) && sequence <= lastSequence)
                return false;

            lastRequestSequenceByPeer[peer] = sequence;
            return true;
        }
    }

    internal bool RouteToRemoteAuthority(
        ClientRequestRecruitment request,
        string originControllerId,
        AuthorityLease lease)
    {
        if (!players.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null)
            return false;

        long sequence;
        lock (sequenceGate)
        {
            if (authorityCommandLease != lease)
            {
                authorityCommandLease = lease;
                nextAuthorityCommandSequence = 0;
            }

            if (nextAuthorityCommandSequence == long.MaxValue)
                throw new InvalidOperationException("Campaign authority recruitment command sequence space is exhausted.");

            sequence = ++nextAuthorityCommandSequence;
        }

        network.Send(authorityPeer, new NetworkCampaignAuthorityRecruitmentCommand(
            lease,
            sequence,
            originControllerId,
            request.MobilePartyId,
            request.TroopsInCart));
        return true;
    }
}
