using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.GameMenus.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.TroopRosters.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.Party.Handlers;

internal class SellPrisonersHandler : IHandler
{
    private const string UnsupportedPlayerRansomMessage =
        "Selling co-op player prisoners through this campaign-authority route is unavailable. No prisoners were sold.";
    private const string StaleSaleMessage =
        "The prisoner sale was not applied because campaign authority changed. Reopen the menu and try again.";
    private const int MaximumPendingResults = 256;
    private const int MaximumRequestStreams = 256;

    private static readonly ILogger logger = LogManager.GetLogger<SellPrisonersHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ITroopRosterInterface troopRosterInterface;
    private readonly IPrisonerSaleProcessor prisonerSaleProcessor;
    private readonly ISendCoalescer sendCoalescer;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ISessionIdentity sessionIdentity;
    private readonly string requestStreamId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, long> routedRequests = new();
    private readonly Dictionary<string, long> executedRequests = new();
    private readonly Dictionary<string, PendingResult> pendingResults = new();
    private readonly Queue<string> pendingResultOrder = new();
    private AuthorityLease stateLease;
    private bool hasStateLease;
    private long nextRequestSequence;
    private long nextAuthorityCommandSequence;
    private long lastAuthorityCommandSequence;
    private AuthorityLease commandSequenceLease;
    private int disposed;

    public SellPrisonersHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ITroopRosterInterface troopRosterInterface,
        IPrisonerSaleProcessor prisonerSaleProcessor,
        ISendCoalescer sendCoalescer = null,
        ICampaignAuthorityContext authorityContext = null,
        IPlayerManager playerManager = null,
        IControllerIdProvider controllerIdProvider = null,
        ISessionIdentity sessionIdentity = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.troopRosterInterface = troopRosterInterface;
        this.prisonerSaleProcessor = prisonerSaleProcessor;
        this.sendCoalescer = sendCoalescer;
        this.authorityContext = authorityContext;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.sessionIdentity = sessionIdentity;

        messageBroker.Subscribe<PrisonersSold>(Handle_PrisonersSold);
        messageBroker.Subscribe<SellPrisoners>(Handle_SellPrisoners);
        messageBroker.Subscribe<NetworkCampaignAuthoritySellPrisonersCommand>(Handle_AuthoritySellPrisoners);
        messageBroker.Subscribe<NetworkCampaignAuthoritySellPrisonersResult>(Handle_AuthoritySellPrisonersResult);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        messageBroker.Unsubscribe<PrisonersSold>(Handle_PrisonersSold);
        messageBroker.Unsubscribe<SellPrisoners>(Handle_SellPrisoners);
        messageBroker.Unsubscribe<NetworkCampaignAuthoritySellPrisonersCommand>(Handle_AuthoritySellPrisoners);
        messageBroker.Unsubscribe<NetworkCampaignAuthoritySellPrisonersResult>(Handle_AuthoritySellPrisonersResult);
        routedRequests.Clear();
        executedRequests.Clear();
        pendingResults.Clear();
        pendingResultOrder.Clear();
    }

    private void Handle_PrisonersSold(MessagePayload<PrisonersSold> payload) =>
        GameThread.RunSafe(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            HandleLocalSale(payload.What);
        }, context: nameof(SellPrisonersHandler));

    private void HandleLocalSale(PrisonersSold sale)
    {
        if (authorityContext == null || playerManager == null || controllerIdProvider == null ||
            string.IsNullOrWhiteSpace(controllerIdProvider.ControllerId) ||
            !playerManager.TryGetPlayer(controllerIdProvider.ControllerId, out var player) || player == null ||
            sale.SellingParty == null || !objectManager.TryGetHandle(sale.SellingParty, out var partyId) ||
            !OwnsParty(player, partyId, sale.SellingParty))
            return;

        var packedRoster = troopRosterInterface.PackTroopRosterData(sale.LeftPrisonerRoster);
        if (!authorityContext.TryGetCurrentLease(out var lease) || !lease.IsValid || lease.IsPaused)
        {
            FlushPrisonRoster(sale.SellingParty);
            if (ModInformation.IsClient)
            {
                // Dedicated servers still accept the original two-field request during session bootstrap.
                network.SendAll(new SellPrisoners(partyId, packedRoster));
                return;
            }

            NotifyPlayer(player, null, StaleSaleMessage);
            return;
        }

        ObserveLease(lease);
        if (nextRequestSequence == long.MaxValue)
            throw new InvalidOperationException("Prisoner sale request sequence space is exhausted.");

        var request = new SellPrisoners(
            partyId,
            packedRoster,
            lease,
            player.ControllerId,
            requestStreamId,
            ++nextRequestSequence);
        FlushPrisonRoster(sale.SellingParty);

        if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
        {
            if (!RunAsCurrentWriter(lease, () => ApplySale(request, player.ControllerId, null, sale.SellingParty, player, false)))
                NotifyPlayer(player, null, StaleSaleMessage);
            return;
        }

        if (ModInformation.IsServer)
        {
            if (!TryRouteToAuthority(request, player, sale.SellingParty, null))
                NotifyPlayer(player, null, StaleSaleMessage);
            return;
        }

        network.SendAll(request);
    }

    private void Handle_SellPrisoners(MessagePayload<SellPrisoners> payload) =>
        GameThread.RunSafe(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            HandleSaleRequest(payload.What, payload.Who as NetPeer);
        },
            context: nameof(SellPrisonersHandler));

    private void HandleSaleRequest(SellPrisoners request, NetPeer requester)
    {
        if (!ModInformation.IsServer || requester == null || playerManager == null || request.SellingPartyId == 0 ||
            !TryGetPeerOwner(requester, request, out var player) ||
            !objectManager.TryGetObjectWithLogging<PartyBase>(request.SellingPartyId, out var sellingParty) ||
            !OwnsParty(player, request.SellingPartyId, sellingParty))
            return;

        FlushPrisonRoster(sellingParty);

        if (request.IsLegacy)
        {
            if (!ModInformation.IsDedicatedAuthority || authorityContext?.IsAuthorityKnown == true)
            {
                NotifyPlayer(player, requester, StaleSaleMessage);
                logger.Information("Legacy prisoner sale rejected outside dedicated authority bootstrap controller={ControllerId}",
                    player.ControllerId);
                return;
            }

            ApplyLegacyDedicatedSale(request, requester, sellingParty, player);
            return;
        }

        if (authorityContext == null || !authorityContext.TryGetCurrentLease(out var lease) ||
            !request.MatchesLease(lease))
        {
            NotifyPlayer(player, requester, StaleSaleMessage);
            return;
        }

        ObserveLease(lease);
        if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
        {
            if (!RunAsCurrentWriter(lease, () =>
            {
                if (!TryGetPeerOwner(requester, request, out var currentPlayer) ||
                    !objectManager.TryGetObject<PartyBase>(request.SellingPartyId, out var currentParty) ||
                    !OwnsParty(currentPlayer, request.SellingPartyId, currentParty))
                    return;

                ApplySale(request, currentPlayer.ControllerId, requester, currentParty, currentPlayer, false);
            }))
                NotifyPlayer(player, requester, StaleSaleMessage);
            return;
        }

        if (!ModInformation.IsListenAuthority) return;

        if (!TryRouteToAuthority(request, player, sellingParty, requester))
            NotifyPlayer(player, requester, StaleSaleMessage);
    }

    private void ApplyLegacyDedicatedSale(SellPrisoners request, NetPeer requester, PartyBase sellingParty, Player player)
    {
        TroopRoster roster = new();
        troopRosterInterface.UpdateWithData(roster, request.LeftPrisonerRosterData, sellingParty.LeaderHero);
        prisonerSaleProcessor.Sell(sellingParty, roster);
        logger.Information(
            "Prisoner sale applied originController={OriginControllerId} writerController={WriterControllerId} epoch={Epoch} authorityId={AuthorityId} requestStream={RequestStreamId} requestSequence={RequestSequence} sellingPartyId={SellingPartyId}",
            request.OriginControllerId,
            stateLease.AuthorityControllerId,
            stateLease.Epoch,
            stateLease.AuthorityId,
            request.RequestStreamId,
            request.RequestSequence,
            request.SellingPartyId);
        FlushPrisonRoster(sellingParty);
        RefreshPlayerMenu(sellingParty, player, requester);
    }

    private bool TryRouteToAuthority(SellPrisoners request, Player origin, PartyBase sellingParty, NetPeer requester)
    {
        if (!ModInformation.IsListenAuthority || authorityContext == null || playerManager == null ||
            !request.MatchesLease(stateLease) || !OwnsParty(origin, request.SellingPartyId, sellingParty) ||
            !authorityContext.TryGetCurrentLease(out var lease) || lease != stateLease ||
            authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease ||
            !playerManager.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null)
            return false;

        Player currentOrigin = null;
        if (requester != null &&
            (!TryGetPeerOwner(requester, request, out currentOrigin) || currentOrigin.ControllerId != origin.ControllerId))
            return false;
        if (!TryAdvanceRequest(routedRequests, request)) return false;
        if (nextAuthorityCommandSequence == long.MaxValue)
            throw new InvalidOperationException("Prisoner sale authority-command sequence space is exhausted.");

        var command = new NetworkCampaignAuthoritySellPrisonersCommand(
            lease,
            ++nextAuthorityCommandSequence,
            request);
        AddPendingResult(request, requester);
        network.Send(authorityPeer, command);
        logger.Information("Prisoner sale routed controller={ControllerId} writer={WriterId} epoch={Epoch} sequence={Sequence}",
            request.OriginControllerId, lease.AuthorityControllerId, lease.Epoch, request.RequestSequence);
        return true;
    }

    private void Handle_AuthoritySellPrisoners(MessagePayload<NetworkCampaignAuthoritySellPrisonersCommand> payload) =>
        GameThread.RunSafe(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            HandleAuthorityCommand(payload.What, payload.Who as NetPeer);
        },
            context: nameof(SellPrisonersHandler));

    private void HandleAuthorityCommand(NetworkCampaignAuthoritySellPrisonersCommand command, NetPeer roomHostPeer)
    {
        if (roomHostPeer == null || authorityContext == null || playerManager == null ||
            !authorityContext.TryGetLocalAuthorityLease(out var lease) || !command.Matches(lease))
            return;

        ObserveLease(lease);

        RunAsCurrentWriter(lease, () =>
        {
            if (!authorityContext.TryGetLocalAuthorityLease(out var currentLease) || currentLease != lease ||
                !CampaignAuthorityCommandSenderValidator.IsRoomHost(roomHostPeer, playerManager, sessionIdentity, network) ||
                !playerManager.TryGetPlayer(command.OriginControllerId, out var player) || player == null ||
                playerManager.IsReservedController(player.ControllerId) ||
                !objectManager.TryGetObject<PartyBase>(command.Request.SellingPartyId, out var sellingParty) ||
                !OwnsParty(player, command.Request.SellingPartyId, sellingParty))
                return;

            if (!CampaignAuthorityCommandSenderValidator.IsRoomHost(roomHostPeer, playerManager, sessionIdentity, network))
                return;

            ApplySale(command.Request, player.ControllerId, null, sellingParty, player, true,
                roomHostPeer, command.Sequence);
        });
    }

    private void Handle_AuthoritySellPrisonersResult(MessagePayload<NetworkCampaignAuthoritySellPrisonersResult> payload) =>
        GameThread.RunSafe(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            HandleAuthorityResult(payload.What, payload.Who as NetPeer);
        },
            context: nameof(SellPrisonersHandler));

    private void HandleAuthorityResult(NetworkCampaignAuthoritySellPrisonersResult result, NetPeer authorityPeer)
    {
        if (!ModInformation.IsListenAuthority || authorityPeer == null || authorityContext == null || playerManager == null ||
            !authorityContext.TryGetCurrentLease(out var lease) || !result.Matches(lease) ||
            authorityContext.TryGetLocalAuthorityLease(out _))
            return;

        if (!playerManager.TryGetPlayer(authorityPeer, out var writer) || writer == null ||
            writer.ControllerId != lease.AuthorityControllerId ||
            !playerManager.TryGetPeer(writer.ControllerId, out var currentAuthorityPeer) ||
            !ReferenceEquals(authorityPeer, currentAuthorityPeer))
            return;

        ObserveLease(lease);
        string key = PendingKey(result.OriginControllerId, result.RequestStreamId, result.RequestSequence);
        if (!pendingResults.TryGetValue(key, out var pending) ||
            pending.Request.RequestSequence != result.RequestSequence ||
            pending.Request.SellingPartyId != result.SellingPartyId)
            return;

        RemovePendingResult(key);
        if (!playerManager.TryGetPlayer(result.OriginControllerId, out var origin) || origin == null)
            return;

        if (result.Applied)
        {
            if (objectManager.TryGetObject<PartyBase>(result.SellingPartyId, out var sellingParty))
                RefreshPlayerMenu(sellingParty, origin, pending.Requester);
            return;
        }

        string reason = string.IsNullOrWhiteSpace(result.Reason) ? StaleSaleMessage : result.Reason;
        NotifyPlayer(origin, pending.Requester, reason);
        logger.Information("Prisoner sale rejected by campaign authority controller={ControllerId} epoch={Epoch} sequence={Sequence} reason={Reason}",
            result.OriginControllerId, lease.Epoch, result.RequestSequence, reason);
    }

    private void ApplySale(
        SellPrisoners request,
        string originControllerId,
        NetPeer requester,
        PartyBase sellingParty,
        Player player,
        bool authorityCommand,
        NetPeer roomHostPeer = null,
        long authorityCommandSequence = 0)
    {
        if (!request.MatchesLease(stateLease)) return;

        if (requester != null && !TryGetPeerOwner(requester, request, out player)) return;
        if (!playerManager.TryGetPlayer(originControllerId, out var currentPlayer) || currentPlayer == null ||
            !OwnsParty(currentPlayer, request.SellingPartyId, sellingParty) ||
            requester != null && !ReferenceEquals(player, currentPlayer))
            return;

        if (authorityCommand &&
            !CampaignAuthorityCommandSenderValidator.IsRoomHost(roomHostPeer, playerManager, sessionIdentity, network))
            return;
        if (authorityCommand && !TryAdvanceAuthorityCommand(stateLease, authorityCommandSequence)) return;
        if (!TryAdvanceRequest(executedRequests, request)) return;

        if (ModInformation.IsClient &&
            TryFindSelectedPlayerPrisoner(request.LeftPrisonerRosterData, sellingParty, out var refusalReason))
        {
            RejectSale(request, currentPlayer, requester, refusalReason, authorityCommand);
            return;
        }

        TroopRoster roster = new();
        troopRosterInterface.UpdateWithData(roster, request.LeftPrisonerRosterData, sellingParty.LeaderHero);
        prisonerSaleProcessor.Sell(sellingParty, roster);
        logger.Information(
            "Prisoner sale applied originController={OriginControllerId} writerController={WriterControllerId} epoch={Epoch} authorityId={AuthorityId} requestStream={RequestStreamId} requestSequence={RequestSequence} sellingPartyId={SellingPartyId}",
            originControllerId,
            stateLease.AuthorityControllerId,
            stateLease.Epoch,
            stateLease.AuthorityId,
            request.RequestStreamId,
            request.RequestSequence,
            request.SellingPartyId);
        FlushPrisonRoster(sellingParty);

        if (authorityCommand)
        {
            network.SendAll(new NetworkCampaignAuthoritySellPrisonersResult(stateLease, request, true, string.Empty));
            return;
        }

        RefreshPlayerMenu(sellingParty, currentPlayer, requester);
    }

    private bool TryFindSelectedPlayerPrisoner(TroopRosterData packedRoster, PartyBase sellingParty, out string reason)
    {
        reason = string.Empty;
        if (packedRoster.Data == null)
        {
            reason = "The prisoner sale could not be validated and was not applied. Reopen the menu and try again.";
            return true;
        }

        foreach (TroopRosterElementData element in packedRoster.Data)
        {
            if (element.Number <= 0) continue;
            if (!objectManager.TryGetObject<CharacterObject>(element.CharacterId, out var character))
            {
                logger.Warning("Prisoner sale rejected because character handle {CharacterId} was unavailable", element.CharacterId);
                reason = "The prisoner sale could not be validated and was not applied. Reopen the menu and try again.";
                return true;
            }

            if (sellingParty.PrisonRoster.GetTroopCount(character) <= 0) continue;
            var hero = character.HeroObject;
            if (hero == null || !playerManager.Contains(hero)) continue;

            reason = UnsupportedPlayerRansomMessage;
            return true;
        }

        return false;
    }

    private void RejectSale(SellPrisoners request, Player player, NetPeer requester, string reason, bool authorityCommand)
    {
        logger.Information("Prisoner sale rejected before roster creation controller={ControllerId} epoch={Epoch} sequence={Sequence} reason={Reason}",
            request.OriginControllerId, stateLease.Epoch, request.RequestSequence, reason);
        if (authorityCommand)
        {
            network.SendAll(new NetworkCampaignAuthoritySellPrisonersResult(stateLease, request, false, reason));
            return;
        }

        NotifyPlayer(player, requester, reason);
    }

    private bool TryGetPeerOwner(NetPeer peer, SellPrisoners request, out Player player)
    {
        player = null;
        return peer != null && playerManager != null &&
            playerManager.TryGetPlayer(peer, out player) && player != null &&
            !playerManager.IsReservedController(player.ControllerId) &&
            (request.IsLegacy || player.ControllerId == request.OriginControllerId) &&
            playerManager.TryGetPeer(player.ControllerId, out var currentPeer) && ReferenceEquals(peer, currentPeer) &&
            playerManager.ValidateCurrentPartySender(peer, player.MobilePartyId);
    }

    private bool OwnsParty(Player player, uint partyId, PartyBase sellingParty)
    {
        return player != null && sellingParty != null && partyId != 0 &&
            objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var mobileParty) &&
            mobileParty != null && ReferenceEquals(mobileParty.Party, sellingParty) &&
            objectManager.TryGetHandle(sellingParty, out var currentPartyId) && currentPartyId == partyId;
    }

    private bool RunAsCurrentWriter(AuthorityLease lease, Action action)
    {
        if (authorityContext == null || !lease.IsValid || lease.IsPaused) return false;
        bool ran = false;
        bool leaseCurrent = authorityContext.TryExecuteWithCurrentLease(lease, () =>
        {
            if (authorityContext.TryGetLocalAuthorityLease(out var currentLease) && currentLease == lease)
            {
                ran = true;
                action();
            }
        });
        return leaseCurrent && ran;
    }

    private void FlushPrisonRoster(PartyBase sellingParty)
    {
        if (sellingParty != null && objectManager.TryGetHandle(sellingParty.PrisonRoster, out var rosterId))
            sendCoalescer?.FlushInstance(rosterId, network);
    }

    private void RefreshPlayerMenu(PartyBase sellingParty, Player player, NetPeer requester)
    {
        var hero = sellingParty?.LeaderHero;
        if (hero == null || player == null || !playerManager.Contains(hero) ||
            !objectManager.TryGetId(hero, out var heroId) || heroId != player.HeroId)
            return;

        var refresh = new RefreshGameMenu(heroId, "town_backstreet");
        if (requester != null)
        {
            if (playerManager.TryGetPeer(player.ControllerId, out var currentPeer) && ReferenceEquals(requester, currentPeer))
                network.Send(requester, refresh);
            return;
        }

        if (controllerIdProvider != null && player.ControllerId == controllerIdProvider.ControllerId)
            messageBroker.Publish(this, refresh);
    }

    private void NotifyPlayer(Player player, NetPeer requester, string text)
    {
        if (player == null || string.IsNullOrWhiteSpace(text)) return;
        var notification = new SendInformationMessage(text);
        if (requester != null)
        {
            if (playerManager.TryGetPeer(player.ControllerId, out var currentPeer) && ReferenceEquals(requester, currentPeer))
                network.Send(requester, notification);
            return;
        }

        if (controllerIdProvider != null && player.ControllerId == controllerIdProvider.ControllerId)
            messageBroker.Publish(this, notification);
    }

    private void AddPendingResult(SellPrisoners request, NetPeer requester)
    {
        string key = PendingKey(request.OriginControllerId, request.RequestStreamId, request.RequestSequence);
        if (pendingResults.ContainsKey(key)) return;
        while (pendingResultOrder.Count >= MaximumPendingResults)
        {
            string expired = pendingResultOrder.Dequeue();
            if (pendingResults.Remove(expired))
                logger.Warning("Evicting an unacknowledged prisoner sale request {RequestKey}", expired);
        }

        pendingResultOrder.Enqueue(key);
        pendingResults[key] = new PendingResult(request, requester);
    }

    private void RemovePendingResult(string key) => pendingResults.Remove(key);

    private bool TryAdvanceRequest(Dictionary<string, long> watermarks, SellPrisoners request)
    {
        if (request.RequestSequence <= 0 || string.IsNullOrWhiteSpace(request.OriginControllerId) ||
            !Guid.TryParseExact(request.RequestStreamId, "N", out _)) return false;

        string key = RequestKey(request.OriginControllerId, request.RequestStreamId);
        if (watermarks.TryGetValue(key, out var last))
        {
            if (request.RequestSequence <= last) return false;
        }
        else if (watermarks.Count >= MaximumRequestStreams)
        {
            logger.Warning("Rejecting prisoner sale request because the authority epoch reached its {MaximumStreams} request-stream limit controller={ControllerId}",
                MaximumRequestStreams, request.OriginControllerId);
            return false;
        }

        watermarks[key] = request.RequestSequence;
        return true;
    }

    private bool TryAdvanceAuthorityCommand(AuthorityLease lease, long sequence)
    {
        if (commandSequenceLease != lease)
        {
            commandSequenceLease = lease;
            lastAuthorityCommandSequence = 0;
        }

        if (sequence <= 0 || sequence <= lastAuthorityCommandSequence) return false;
        lastAuthorityCommandSequence = sequence;
        return true;
    }

    private void ObserveLease(AuthorityLease lease)
    {
        if (hasStateLease && stateLease == lease) return;
        hasStateLease = true;
        stateLease = lease;
        routedRequests.Clear();
        executedRequests.Clear();
        pendingResults.Clear();
        pendingResultOrder.Clear();
        commandSequenceLease = lease;
        lastAuthorityCommandSequence = 0;
        nextAuthorityCommandSequence = 0;
    }

    private static string RequestKey(string controllerId, string streamId) => controllerId + "\0" + streamId;

    private static string PendingKey(string controllerId, string streamId, long sequence) =>
        RequestKey(controllerId, streamId) + "\0" + sequence;

    private sealed class PendingResult
    {
        public SellPrisoners Request { get; }
        public NetPeer Requester { get; }

        public PendingResult(SellPrisoners request, NetPeer requester)
        {
            Request = request;
            Requester = requester;
        }
    }
}
