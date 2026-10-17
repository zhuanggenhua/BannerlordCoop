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
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;

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
        ICampaignAuthorityContext authorityContext = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.troopRosterInterface = troopRosterInterface;
        this.players = players;
        this.authorityContext = authorityContext;

        messageBroker.Subscribe<ClientRequestRecruitment>(HandleOnRecruitmentDone);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<ClientRequestRecruitment>(HandleOnRecruitmentDone);
        lock (sequenceGate)
        {
            lastRequestSequenceByPeer.Clear();
            authorityCommandLease = default;
            nextAuthorityCommandSequence = 0;
        }
    }

    private void HandleOnRecruitmentDone(MessagePayload<ClientRequestRecruitment> payload)
    {
        var data = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!players.ValidateCurrentPartySender(payload.Who, data.MobilePartyId)) return;

                if (!TryGetRequestLease(data, out var lease)) return;
                if (payload.Who is NetPeer requestPeer && !TryAdvanceRequestSequence(requestPeer, data.RequestSequence))
                    return;

                if (authorityContext?.IsAuthorityKnown == true)
                {
                    bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var localLease) &&
                        localLease == lease;
                    if (localAuthority)
                    {
                        troopRosterInterface.HandleOnRecruitmentDone(data.MobilePartyId, data.TroopsInCart);
                        return;
                    }

                    if (lease.IsValid && !lease.IsPaused && payload.Who is NetPeer peer &&
                        players.TryGetPlayer(peer, out var player) && player != null)
                        RouteToRemoteAuthority(data, player.ControllerId, lease);
                    return;
                }

                troopRosterInterface.HandleOnRecruitmentDone(data.MobilePartyId, data.TroopsInCart);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(ClientRequestRecruitment));
            }
        });
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
