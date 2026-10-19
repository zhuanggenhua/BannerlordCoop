using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.MobileParties;
using Common.Util;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Handlers;

internal sealed class CampaignSettlementLeaveHandler : IHandler
{
    private readonly ILogger logger = LogManager.GetLogger<CampaignSettlementLeaveHandler>();
    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly IObjectManager objects;
    private readonly IPlayerManager players;
    private readonly ICampaignAuthorityContext authority;
    private readonly ISessionIdentity session;
    private readonly IControllerIdProvider controller;
    private readonly ICampaignSettlementLeave leave;
    private readonly ISettlementInterface settlements;
    private readonly Dictionary<string, long> routed = new Dictionary<string, long>();
    private readonly Dictionary<string, long> executed = new Dictionary<string, long>();
    private readonly Dictionary<string, long> applied = new Dictionary<string, long>();
    private AuthorityLease watermarkLease;
    private long nextRequest;
    private volatile bool disposed;
    private readonly string requestStreamId = Guid.NewGuid().ToString("N");

    public CampaignSettlementLeaveHandler(IMessageBroker broker, INetwork network, IObjectManager objects,
        IPlayerManager players, ICampaignAuthorityContext authority, ISessionIdentity session,
        IControllerIdProvider controller, ICampaignSettlementLeave leave, ISettlementInterface settlements)
    {
        this.broker = broker;
        this.network = network;
        this.objects = objects;
        this.players = players;
        this.authority = authority;
        this.session = session;
        this.controller = controller;
        this.leave = leave;
        this.settlements = settlements;
        broker.Subscribe<EndSettlementEncounterAttempted>(HandleAttempt);
        broker.Subscribe<CampaignSettlementLeaveRequest>(HandleRequest);
        broker.Subscribe<CampaignSettlementLeaveCommand>(HandleCommand);
        broker.Subscribe<CampaignSettlementLeaveApplied>(HandleApplied);
    }

    public void Dispose()
    {
        disposed = true;
        broker.Unsubscribe<EndSettlementEncounterAttempted>(HandleAttempt);
        broker.Unsubscribe<CampaignSettlementLeaveRequest>(HandleRequest);
        broker.Unsubscribe<CampaignSettlementLeaveCommand>(HandleCommand);
        broker.Unsubscribe<CampaignSettlementLeaveApplied>(HandleApplied);
    }

    private void HandleAttempt(MessagePayload<EndSettlementEncounterAttempted> payload) =>
        GameThread.EnqueueSafe(() =>
        {
            var attempt = payload.What;
            if (disposed || string.IsNullOrWhiteSpace(session.RoomHostControllerId) ||
                !authority.IsAuthorityKnown || !authority.TryGetCurrentLease(out var lease) ||
                !lease.IsValid || lease.IsPaused || attempt.ObservedLease != lease ||
                controller.ControllerId == lease.AuthorityControllerId ||
                !objects.TryGetHandle(attempt.Party, out var partyId) ||
                !Controls(controller.ControllerId, partyId, out var party) || !ReferenceEquals(party, attempt.Party) ||
                !objects.TryGetHandle(party.CurrentSettlement, out var settlementId)) return;

            if (nextRequest == long.MaxValue) throw new InvalidOperationException("Settlement leave request sequence exhausted.");
            var request = new CampaignSettlementLeaveRequest(lease, controller.ControllerId,
                ++nextRequest, partyId, settlementId, requestStreamId);
            logger.Information("Settlement leave requested controller={Controller} epoch={Epoch} sequence={Sequence} party={Party}",
                request.ControllerId, request.Epoch, request.Sequence, request.PartyId);
            if (IsRoomHost) Route(request);
            else network.SendAll(request);
        }, context: nameof(CampaignSettlementLeaveHandler));

    private bool IsRoomHost => !disposed && ModInformation.IsListenAuthority &&
        controller.ControllerId == session.RoomHostControllerId;

    private void HandleRequest(MessagePayload<CampaignSettlementLeaveRequest> payload) =>
        GameThread.EnqueueSafe(() =>
        {
            if (!IsRoomHost || payload.Who is not NetPeer peer || payload.What == null ||
                !players.TryGetPlayer(peer, out var player) || player == null ||
                player.ControllerId != payload.What.ControllerId ||
                !players.TryGetPeer(player.ControllerId, out var currentPeer) ||
                !ReferenceEquals(peer, currentPeer)) return;
            Route(payload.What);
        }, context: nameof(CampaignSettlementLeaveHandler));

    private void Route(CampaignSettlementLeaveRequest request)
    {
        if (!authority.TryGetCurrentLease(out var lease) || !request.Matches(lease) ||
            !Controls(request.ControllerId, request.PartyId, out _) ||
            !Advance(routed, lease, request)) return;

        if (authority.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
            Execute(request);
        else if (players.TryGetPeer(lease.AuthorityControllerId, out var peer) && peer != null)
        {
            network.Send(peer, new CampaignSettlementLeaveCommand(request));
            logger.Information("Settlement leave routed controller={Controller} writer={Writer} epoch={Epoch} sequence={Sequence}",
                request.ControllerId, lease.AuthorityControllerId, request.Epoch, request.Sequence);
        }
    }

    private void HandleCommand(MessagePayload<CampaignSettlementLeaveCommand> payload) =>
        GameThread.EnqueueSafe(() =>
        {
            if (disposed) return;
            if (payload.Who is NetPeer peer &&
                CampaignAuthorityCommandSenderValidator.IsRoomHost(peer, players, session))
                Execute(payload.What?.Request);
            else LogRejected(payload.What?.Request, "room_host_peer_changed");
        }, context: nameof(CampaignSettlementLeaveHandler));

    private void Execute(CampaignSettlementLeaveRequest request)
    {
        if (disposed || request == null || !authority.TryGetLocalAuthorityLease(out var lease) || !request.Matches(lease))
        {
            LogRejected(request, "stale_or_frozen_authority");
            return;
        }
        if (!Controls(request.ControllerId, request.PartyId, out var party) ||
            !objects.TryGetHandle(party.CurrentSettlement, out var settlementId) ||
            settlementId != request.SettlementId)
        {
            LogRejected(request, "party_or_settlement_changed");
            return;
        }

        authority.TryExecuteWithCurrentLease(lease, () =>
        {
            if (!Advance(executed, lease, request))
            {
                LogRejected(request, "duplicate_or_reordered_sequence");
                return;
            }
            var previousSettlement = party.CurrentSettlement;
            if (!leave.Apply(party))
            {
                logger.Warning("Settlement leave was not applied controller={Controller} epoch={Epoch} sequence={Sequence}",
                    request.ControllerId, request.Epoch, request.Sequence);
                return;
            }
            network.SendAll(new CampaignSettlementLeaveApplied(request));
            broker.Publish(this, new SettlementEncounterLeaveApplied(party, previousSettlement));
            logger.Information("Settlement leave applied controller={Controller} epoch={Epoch} sequence={Sequence} party={Party}",
                request.ControllerId, request.Epoch, request.Sequence, request.PartyId);
        });
    }

    private void HandleApplied(MessagePayload<CampaignSettlementLeaveApplied> payload) =>
        GameThread.RunSafe(() =>
        {
            var request = payload.What?.Request;
            if (disposed || request == null || !authority.TryGetCurrentLease(out var lease) || !request.Matches(lease) ||
                authority.TryGetLocalAuthorityLease(out _) ||
                !Controls(request.ControllerId, request.PartyId, out var party) ||
                (party.CurrentSettlement != null &&
                    (!objects.TryGetHandle(party.CurrentSettlement, out var town) || town != request.SettlementId)) ||
                !Advance(applied, lease, request)) return;

            using (new AllowedThread())
            {
                settlements.PartyLeaveSettlement(party);
                if (request.ControllerId == controller.ControllerId && ReferenceEquals(party, MobileParty.MainParty))
                    settlements.EndSettlementEncounter();
            }
        }, context: nameof(CampaignSettlementLeaveHandler));

    private void LogRejected(CampaignSettlementLeaveRequest request, string reason) =>
        logger.Information("Settlement leave rejected reason={Reason} controller={Controller} epoch={Epoch} sequence={Sequence}",
            reason, request?.ControllerId, request?.Epoch, request?.Sequence);

    private bool Controls(string controllerId, uint partyId, out MobileParty party)
    {
        party = null;
        return !string.IsNullOrWhiteSpace(controllerId) && players.TryGetPlayer(controllerId, out var player) &&
            player != null && (controllerId == session.RoomHostControllerId || !players.IsReservedController(controllerId)) &&
            objects.TryGetObject<MobileParty>(player.MobilePartyId, out party) &&
            objects.TryGetHandle(party, out var registeredHandle) && registeredHandle == partyId;
    }

    private bool Advance(Dictionary<string, long> watermarks, AuthorityLease lease, CampaignSettlementLeaveRequest request)
    {
        if (watermarkLease != lease)
        {
            watermarkLease = lease;
            routed.Clear();
            executed.Clear();
            applied.Clear();
        }
        string stream = request.ControllerId + ":" + request.RequestStreamId;
        if (watermarks.TryGetValue(stream, out var last) && request.Sequence <= last) return false;
        watermarks[stream] = request.Sequence;
        return true;
    }
}
