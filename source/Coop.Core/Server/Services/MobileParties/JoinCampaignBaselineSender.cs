using Common.Logging;
using Common.Network;
using Coop.Core.Server.Services.MobileParties.Messages;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Server.Services.MobileParties;

public interface IJoinCampaignBaselineSender
{
    void Send(NetPeer peer);
    void Send(NetPeer peer, string baselineRequestId);
}

internal sealed class JoinCampaignBaselineSender : IJoinCampaignBaselineSender
{
    private static readonly ILogger Logger = LogManager.GetLogger<JoinCampaignBaselineSender>();

    private readonly INetwork network;
    private readonly IMapTimeTrackerInterface mapTimeTrackerInterface;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;
    private readonly ITimeControlInterface timeControlInterface;
    private readonly IPlayerPartyTroopXpBaselineProvider troopXpBaselineProvider;
    private readonly IPartyBehaviorWireMapper partyBehaviorWireMapper;
    private readonly ICampaignAuthorityContext authorityContext;

    public JoinCampaignBaselineSender(
        INetwork network,
        IMapTimeTrackerInterface mapTimeTrackerInterface,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot,
        ITimeControlInterface timeControlInterface,
        IPlayerPartyTroopXpBaselineProvider troopXpBaselineProvider,
        IPartyBehaviorWireMapper partyBehaviorWireMapper,
        ICampaignAuthorityContext authorityContext)
    {
        this.network = network;
        this.mapTimeTrackerInterface = mapTimeTrackerInterface;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;
        this.timeControlInterface = timeControlInterface;
        this.troopXpBaselineProvider = troopXpBaselineProvider;
        this.partyBehaviorWireMapper = partyBehaviorWireMapper;
        this.authorityContext = authorityContext;
    }

    public void Send(NetPeer peer) => Send(peer, null);

    public void Send(NetPeer peer, string baselineRequestId)
    {
        var campaignObjectManager = Campaign.Current?.CampaignObjectManager;
        var parties = campaignObjectManager?.MobileParties;
        var settlements = campaignObjectManager?.Settlements;
        if (parties == null ||
            settlements == null ||
            !mapTimeTrackerInterface.TryGetCurrentTicks(out long serverTicks))
        {
            throw new InvalidOperationException("Cannot capture a join baseline without a loaded campaign");
        }

        var activeParties = new List<MobileParty>(parties.Count);
        for (int i = 0; i < parties.Count; i++)
        {
            MobileParty party = parties[i];
            if (party?.IsActive == true) activeParties.Add(party);
        }

        var liveParties = new HashSet<MobileParty>(activeParties);
        var liveSettlements = new HashSet<Settlement>(settlements);
        var partyStates = new NetworkMobilePartyJoinState[activeParties.Count];
        TroopRosterXpBaseline[] troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
        bool isComplete = true;
        for (int i = 0; i < activeParties.Count; i++)
        {
            MobileParty party = activeParties[i];
            if (!mobilePartyBehaviorSnapshot.TryCreateJoinState(
                party,
                liveParties,
                liveSettlements,
                out MobilePartyJoinState state,
                out string failure))
            {
                Logger.Warning(
                    "Could not capture a complete join baseline for party {Party}: {Failure}",
                    party?.StringId,
                    failure);
                isComplete = false;
                break;
            }

            PartyBehaviorUpdateData behavior = state.Behavior;
            behavior.ForcePosition = true;
            if (!partyBehaviorWireMapper.TryToNetwork(behavior, out var networkBehavior))
            {
                Logger.Warning("Could not map a join baseline for party {Party} to network handles", party?.StringId);
                isComplete = false;
                break;
            }
            partyStates[i] = new NetworkMobilePartyJoinState(state, networkBehavior);
        }

        if (isComplete && !troopXpBaselineProvider.TryCapture(peer, out troopXpBaselines))
        {
            Logger.Warning("Could not capture the joining player's troop XP baseline");
            isComplete = false;
        }

        if (isComplete == false)
        {
            partyStates = Array.Empty<NetworkMobilePartyJoinState>();
            troopXpBaselines = Array.Empty<TroopRosterXpBaseline>();
        }

        AuthorityLease? committedPausedCheckpointLease = null;
        if (isComplete && authorityContext.TryGetCurrentLease(out var currentLease) &&
            currentLease.IsValid && currentLease.IsPaused)
        {
            committedPausedCheckpointLease = currentLease;
        }

        network.SendImmediate(
            peer,
            new NetworkJoinCampaignBaseline(
                serverTicks,
                timeControlInterface.GetTimeControl(),
                partyStates,
                isComplete,
                troopXpBaselines,
                committedPausedCheckpointLease,
                baselineRequestId));

        if (committedPausedCheckpointLease.HasValue)
        {
            AuthorityLease lease = committedPausedCheckpointLease.Value;
            Logger.Information(
                "Sent complete paused-checkpoint join baseline to peer {PeerId} for room {RoomId}, " +
                "authority {AuthorityId}, epoch {Epoch}, request {RequestId}",
                peer?.Id,
                lease.RoomId,
                lease.AuthorityId,
                lease.Epoch,
                baselineRequestId);
        }
    }
}
