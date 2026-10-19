using Common;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client;
using Coop.Core.Client.Messages;
using Coop.Core.Client.States;
#if DEBUG
using Coop.Core.Common.Commands;
#endif
using Coop.Core.Server.Services.MobileParties.Messages;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using System;

namespace Coop.Core.Client.Services.MobileParties.Handlers;

/// <summary>
/// Applies an authoritative time and mobile-party baseline while a client is joining.
/// </summary>
public sealed class JoinCampaignBaselineHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly IMapTimeTrackerInterface mapTimeTrackerInterface;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;
    private readonly ITimeControlInterface timeControlInterface;
    private readonly IPlayerPartyTroopXpBaselineApplier troopXpBaselineApplier;
    private readonly IPartyBehaviorWireMapper partyBehaviorWireMapper;
    private readonly INetwork network;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly Func<IClientLogic> getClientLogic;
    private volatile bool disposed;

    public JoinCampaignBaselineHandler(
        IMessageBroker messageBroker,
        IMapTimeTrackerInterface mapTimeTrackerInterface,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot,
        ITimeControlInterface timeControlInterface,
        IPlayerPartyTroopXpBaselineApplier troopXpBaselineApplier,
        IPartyBehaviorWireMapper partyBehaviorWireMapper,
        INetwork network,
        ICampaignAuthorityContext authorityContext,
        Func<IClientLogic> getClientLogic)
    {
        this.messageBroker = messageBroker;
        this.mapTimeTrackerInterface = mapTimeTrackerInterface;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;
        this.timeControlInterface = timeControlInterface;
        this.troopXpBaselineApplier = troopXpBaselineApplier;
        this.partyBehaviorWireMapper = partyBehaviorWireMapper;
        this.network = network;
        this.authorityContext = authorityContext;
        this.getClientLogic = getClientLogic;

        messageBroker.Subscribe<NetworkJoinCampaignBaseline>(Handle);
    }

    public void Dispose()
    {
        disposed = true;
        messageBroker.Unsubscribe<NetworkJoinCampaignBaseline>(Handle);
    }

    private void Handle(MessagePayload<NetworkJoinCampaignBaseline> payload)
    {
        if (disposed) return;

        var baseline = payload.What;
        var sourcePeer = payload.Who as NetPeer;
        if (!IsCurrentRoomHostPeer(sourcePeer)) return;

        bool hasPausedCheckpoint = baseline.HasCommittedPausedCheckpointLease;
        bool requiresCorrelation = hasPausedCheckpoint ||
            !string.IsNullOrWhiteSpace(baseline.BaselineRequestId);
        AuthorityLease checkpointLease = default;
        if (hasPausedCheckpoint && !TryValidatePausedCheckpoint(baseline, sourcePeer, out checkpointLease))
        {
            // A stale lease or old host must not consume the response slot for the current join.
            return;
        }

        GameThread.RunSafe(() =>
        {
            if (disposed || !IsCurrentRoomHostPeer(sourcePeer)) return;
            if (hasPausedCheckpoint &&
                (!TryValidatePausedCheckpoint(baseline, sourcePeer, out var currentCheckpointLease) ||
                 currentCheckpointLease != checkpointLease)) return;

            IClientLogic currentLogic = null;
            CampaignState correlatedCampaignState = null;
            if (requiresCorrelation)
            {
                currentLogic = getClientLogic();
                correlatedCampaignState = currentLogic?.State as CampaignState;
                if (!IsCurrentCampaignStateRequest(
                    currentLogic,
                    correlatedCampaignState,
                    baseline.BaselineRequestId)) return;
            }

            if (!baseline.IsComplete)
            {
                if (disposed || !IsCurrentRoomHostPeer(sourcePeer) ||
                    (requiresCorrelation && !IsCurrentCampaignStateRequest(
                        currentLogic,
                        correlatedCampaignState,
                        baseline.BaselineRequestId))) return;
                messageBroker.Publish(this, new JoinCampaignBaselineApplied(
                    false,
                    baseline.BaselineRequestId));
                return;
            }

#if DEBUG
            JoinDebugCommands.ForceArmedInactivePartyDeficit();
#endif
            bool success = TryMapPartyStates(baseline.PartyStates, out var partyStates) &&
                mobilePartyBehaviorSnapshot.TryApplyJoinBaseline(
                    partyStates,
                    () =>
                    {
                        timeControlInterface.ClientSetTimeControl(baseline.TimeControlMode);
                        mapTimeTrackerInterface.ApplyCampaignJoinBaseline(baseline.ServerTicks);
                    }) &&
                troopXpBaselineApplier.TryApply(baseline.TroopXpBaselines);

            AuthorityLease? pausedCheckpointAttestation = null;
            if (success && hasPausedCheckpoint)
            {
                if (!TryValidatePausedCheckpoint(baseline, sourcePeer, out var appliedCheckpointLease) ||
                    appliedCheckpointLease != checkpointLease)
                {
                    return;
                }
                else
                {
                    pausedCheckpointAttestation = appliedCheckpointLease;
                }
            }

            if (disposed || !IsCurrentRoomHostPeer(sourcePeer) ||
                (requiresCorrelation && !IsCurrentCampaignStateRequest(
                    currentLogic,
                    correlatedCampaignState,
                    baseline.BaselineRequestId))) return;
            if (hasPausedCheckpoint &&
                (!TryValidatePausedCheckpoint(baseline, sourcePeer, out var finalCheckpointLease) ||
                 finalCheckpointLease != checkpointLease)) return;

            messageBroker.Publish(this, new JoinCampaignBaselineApplied(
                success,
                baseline.BaselineRequestId,
                pausedCheckpointAttestation,
                success && hasPausedCheckpoint ? sourcePeer : null));
        }, context: nameof(JoinCampaignBaselineHandler));
    }

    private bool TryValidatePausedCheckpoint(
        NetworkJoinCampaignBaseline baseline,
        NetPeer sourcePeer,
        out AuthorityLease lease)
    {
        lease = default;
        if (sourcePeer == null || network is not IRoomHostPeerProvider peerProvider ||
            !peerProvider.IsCurrentRoomHostPeer(sourcePeer) ||
            !NetworkJoinCampaignBaseline.HasValidBaselineRequestId(baseline.BaselineRequestId) ||
            !baseline.TryGetCommittedPausedCheckpointLease(out var stampedLease) ||
            !authorityContext.TryGetCurrentLease(out var currentLease) ||
            !currentLease.IsValid || !currentLease.IsPaused || currentLease != stampedLease)
        {
            return false;
        }

        lease = stampedLease;
        return true;
    }

    private bool IsCurrentRoomHostPeer(NetPeer peer) =>
        peer != null && network is IRoomHostPeerProvider peerProvider &&
        peerProvider.IsCurrentRoomHostPeer(peer);

    private static bool IsCurrentCampaignStateRequest(
        IClientLogic logic,
        CampaignState campaignState,
        string requestId) =>
        logic != null && campaignState != null &&
        ReferenceEquals(logic.State, campaignState) &&
        campaignState.CanAcceptBaseline(requestId);

    private bool TryMapPartyStates(NetworkMobilePartyJoinState[] source, out MobilePartyJoinState[] destination)
    {
        destination = new MobilePartyJoinState[source?.Length ?? 0];
        if (source == null) return false;

        for (int i = 0; i < source.Length; i++)
        {
            if (!partyBehaviorWireMapper.TryFromNetwork(source[i].Behavior, out var behavior)) return false;
            destination[i] = source[i].ToLocal(behavior);
        }
        return true;
    }
}
