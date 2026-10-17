using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.Actions.Messages;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Actions.Handlers;

internal sealed class PlayerRelationChangeHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerRelationChangeHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly object sequenceGate = new();
    private readonly Dictionary<string, (NetPeer Peer, long Sequence)> requestSequences = new();
    private AuthorityLease commandLease;
    private long nextAuthorityCommandSequence;
    private AuthorityLease lastAuthorityCommandLease;
    private long lastAuthorityCommandSequence;

    public PlayerRelationChangeHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IControllerIdProvider controllerIdProvider,
        ICampaignAuthorityContext authorityContext)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.authorityContext = authorityContext;

        messageBroker.Subscribe<PlayerRelationChangeRequested>(Handle_LocalRequest);
        messageBroker.Subscribe<NetworkPlayerRelationChangeRequest>(Handle_NetworkRequest);
        messageBroker.Subscribe<NetworkCampaignAuthorityPlayerRelationCommand>(Handle_AuthorityCommand);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerRelationChangeRequested>(Handle_LocalRequest);
        messageBroker.Unsubscribe<NetworkPlayerRelationChangeRequest>(Handle_NetworkRequest);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityPlayerRelationCommand>(Handle_AuthorityCommand);
        lock (sequenceGate)
        {
            requestSequences.Clear();
            commandLease = default;
            nextAuthorityCommandSequence = 0;
            lastAuthorityCommandLease = default;
            lastAuthorityCommandSequence = 0;
        }
    }

    private void Handle_LocalRequest(MessagePayload<PlayerRelationChangeRequested> payload)
    {
        var localRequest = payload.What;
        if (localRequest.SourceHero == null || localRequest.TargetHero == null ||
            !objectManager.TryGetIdWithLogging(localRequest.SourceHero, out var sourceHeroId) ||
            !objectManager.TryGetIdWithLogging(localRequest.TargetHero, out var targetHeroId))
            return;

        var request = new NetworkPlayerRelationChangeRequest(
            sourceHeroId,
            targetHeroId,
            localRequest.Relation,
            localRequest.ShowNotification,
            localRequest.RequestSequence,
            localRequest.Lease);

        if (!ModInformation.IsServer)
        {
            network.SendAll(request);
            return;
        }

        string controllerId = controllerIdProvider.ControllerId;
        if (string.IsNullOrWhiteSpace(controllerId) ||
            !playerManager.TryGetPlayer(controllerId, out var player) ||
            !PlayerOwnsRequest(player, request))
            return;

        GameThread.RunSafe(
            () => { HandleValidatedRequest(request, player, peer: null); },
            context: nameof(PlayerRelationChangeHandler));
    }

    private void Handle_NetworkRequest(MessagePayload<NetworkPlayerRelationChangeRequest> payload)
    {
        if (payload.Who is not NetPeer peer ||
            !TryGetPeerOwner(peer, payload.What, out var player))
            return;

        GameThread.RunSafe(
            () =>
            {
                HandleNetworkRequest(peer, payload.What);
            },
            context: nameof(PlayerRelationChangeHandler));
    }

    internal bool HandleNetworkRequest(NetPeer peer, NetworkPlayerRelationChangeRequest request)
    {
        if (!TryGetPeerOwner(peer, request, out var player)) return false;
        return HandleValidatedRequest(request, player, peer);
    }

    private bool HandleValidatedRequest(
        NetworkPlayerRelationChangeRequest request,
        Player player,
        NetPeer peer)
    {
        if (!PlayerOwnsRequest(player, request)) return false;

        bool authorityKnown = authorityContext.IsAuthorityKnown;
        if (!TryGetRequestLease(request, out var lease)) return false;

        if (!TryAdvanceRequestSequence(player.ControllerId, peer, request.RequestSequence))
            return false;

        if (!authorityKnown)
        {
            if (CampaignSimulationAuthorityPolicy.CanRunWorldSimulation())
            {
                ApplyRequest(request, player);
                return true;
            }
            return false;
        }

        if (authorityContext.TryGetLocalAuthorityLease(out var localLease) && localLease == lease)
        {
            ApplyRequest(request, player);
            return true;
        }

        // Only the room host may validate and relay a request to a remote authority.
        if (ModInformation.IsServer)
            return RouteToAuthority(request, player.ControllerId, lease);

        return false;
    }

    private void Handle_AuthorityCommand(MessagePayload<NetworkCampaignAuthorityPlayerRelationCommand> payload)
    {
        var command = payload.What;
        if (!authorityContext.TryGetLocalAuthorityLease(out var lease) || !command.Matches(lease) ||
            !playerManager.TryGetPlayer(command.OriginControllerId, out var player) ||
            !PlayerOwnsRequest(player, command.Request) ||
            !TryAdvanceAuthorityCommandSequence(lease, command.Sequence))
            return;

        GameThread.RunSafe(
            () =>
            {
                if (!authorityContext.TryGetLocalAuthorityLease(out var currentLease) || currentLease != lease ||
                    !playerManager.TryGetPlayer(command.OriginControllerId, out var currentPlayer) ||
                    !PlayerOwnsRequest(currentPlayer, command.Request))
                    return;

                ApplyRequest(command.Request, currentPlayer);
            },
            context: nameof(PlayerRelationChangeHandler));
    }

    internal bool TryGetRequestLease(NetworkPlayerRelationChangeRequest request, out AuthorityLease lease)
    {
        if (!authorityContext.IsAuthorityKnown)
        {
            lease = default;
            return true;
        }

        return authorityContext.TryGetCurrentLease(out lease) && request.MatchesLease(lease);
    }

    internal bool RouteToAuthority(NetworkPlayerRelationChangeRequest request, string originControllerId, AuthorityLease lease)
    {
        if (!playerManager.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null)
            return false;

        lock (sequenceGate)
        {
            if (commandLease != lease)
            {
                commandLease = lease;
                nextAuthorityCommandSequence = 0;
            }

            if (nextAuthorityCommandSequence == long.MaxValue)
                throw new InvalidOperationException("Campaign authority relation-command sequence space is exhausted.");

            long sequence = ++nextAuthorityCommandSequence;
            network.Send(authorityPeer, new NetworkCampaignAuthorityPlayerRelationCommand(
                lease,
                sequence,
                originControllerId,
                request));
        }

        return true;
    }

    internal bool TryAdvanceRequestSequence(string controllerId, NetPeer peer, long sequence)
    {
        if (sequence <= 0) return false;

        lock (sequenceGate)
        {
            if (requestSequences.TryGetValue(controllerId, out var previous) &&
                ReferenceEquals(previous.Peer, peer) && sequence <= previous.Sequence)
                return false;

            requestSequences[controllerId] = (peer, sequence);
            return true;
        }
    }

    internal bool TryAdvanceAuthorityCommandSequence(AuthorityLease lease, long sequence)
    {
        lock (sequenceGate)
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

    private bool TryGetPeerOwner(NetPeer peer, NetworkPlayerRelationChangeRequest request, out Player player)
    {
        player = null;
        return playerManager.TryGetPlayer(peer, out player) && player != null &&
            !playerManager.IsReservedController(player.ControllerId) &&
            player.HeroId == request.SourceHeroId &&
            playerManager.TryGetPeer(player.ControllerId, out var currentPeer) &&
            ReferenceEquals(currentPeer, peer);
    }

    private static bool PlayerOwnsRequest(Player player, NetworkPlayerRelationChangeRequest request) =>
        player != null && player.HeroId == request.SourceHeroId &&
        !string.IsNullOrWhiteSpace(request.TargetHeroId);

    private void ApplyRequest(NetworkPlayerRelationChangeRequest request, Player player)
    {
        if (!objectManager.TryGetObjectWithLogging<Hero>(player.HeroId, out var sourceHero) ||
            !objectManager.TryGetObjectWithLogging<Hero>(request.TargetHeroId, out var targetHero))
            return;

        Hero previousResolvedHero = ResolvedMainHeroContext.ResolvedMainHero;
        try
        {
            ResolvedMainHeroContext.ResolvedMainHero = sourceHero;
            ChangeRelationAction.ApplyInternal(
                sourceHero,
                targetHero,
                request.Relation,
                request.ShowNotification,
                ChangeRelationAction.ChangeRelationDetail.Default);
        }
        finally
        {
            ResolvedMainHeroContext.ResolvedMainHero = previousResolvedHero;
        }
    }
}
