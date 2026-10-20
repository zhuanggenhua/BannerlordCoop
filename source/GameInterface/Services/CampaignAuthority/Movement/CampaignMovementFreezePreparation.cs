using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using Serilog;
using Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.CampaignAuthority.Movement;

public interface ICampaignMovementFreezePreparation
{
    bool TryPrepareForFreeze(AuthorityLease lease);
}

public sealed class CampaignMovementFreezePreparation : ICampaignMovementFreezePreparation
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignMovementFreezePreparation>();
    private readonly ICampaignAuthorityContext authority;
    private readonly IMobilePartyBehaviorSnapshot snapshot;
    private readonly IPartyBehaviorWireMapper mapper;
    private readonly IMessageBroker messages;

    public CampaignMovementFreezePreparation(ICampaignAuthorityContext authority, IMobilePartyBehaviorSnapshot snapshot,
        IPartyBehaviorWireMapper mapper, IMessageBroker messages)
    {
        this.authority = authority;
        this.snapshot = snapshot;
        this.mapper = mapper;
        this.messages = messages;
    }

    public bool TryPrepareForFreeze(AuthorityLease lease)
    {
        if (!GameThread.Instance.IsGameThread || AllowedThread.IsThisThreadAllowed() ||
            !authority.TryGetLocalAuthorityLease(out var current) || current != lease || !lease.IsValid || lease.IsPaused)
            return false;
        bool prepared = false;
        try
        {
            return authority.TryExecuteWithCurrentLease(lease, () => prepared = Prepare(lease)) && prepared;
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Pre-freeze navigation failed at epoch {Epoch}; handoff was refused", lease.Epoch);
            return false;
        }
    }

    private bool Prepare(AuthorityLease lease)
    {
        var manager = Campaign.Current?.CampaignObjectManager;
        if (manager?.MobileParties == null || manager.Settlements == null) return false;
        var parties = new HashSet<MobileParty>(manager.MobileParties.Where(party => party?.IsActive == true));
        var settlements = new HashSet<Settlement>(manager.Settlements);
        var states = new List<MobilePartyJoinState>();
        var repairs = new List<(MobileParty party, CampaignVec2 destination)>();
        foreach (var party in parties)
        {
            MobilePartyJoinState state;
            if (!snapshot.TryCreateAuthorityState(party, parties, settlements, out state))
            {
                var target = party.MoveTargetParty;
                if (party.PartyMoveMode != MoveModeType.Party || target == null || parties.Contains(target) ||
                    party.IsPlayerParty() || party == MobileParty.MainParty || party.Ai == null ||
                    ReferenceEquals(party.TargetParty, target) || Targets(party.Ai.AiBehaviorInteractable, target))
                    return Reject(party, lease);
                // Add only the missing navigation target for validation; its other uses remain forbidden.
                var navigationTargets = new HashSet<MobileParty>(parties) { target };
                if (!snapshot.TryCreateAuthorityState(party, navigationTargets, settlements, out state) ||
                    !Finite(target.Position)) return Reject(party, lease);
                var behavior = state.Behavior;
                behavior.PartyMoveMode = MoveModeType.Point;
                behavior.MoveTargetPartyId = null;
                behavior.MoveTargetPoint = target.Position;
                state.Behavior = behavior;
                repairs.Add((party, target.Position));
            }
            if (!Finite(state.Behavior.PartyPosition) || !mapper.TryToNetwork(state.Behavior, out var wire) ||
                wire.MobilePartyId == 0) return Reject(party, lease);
            states.Add(state);
        }
        // Resolve every original reference and prove exact coverage before changing the first NPC.
        if (!snapshot.TryPrepareAuthorityBaseline(states.ToArray(), out _)) return false;
        foreach (var repair in repairs)
            NavigationHelper.IsPositionValidForNavigationType(repair.destination, repair.party.NavigationCapability);
        foreach (var repair in repairs)
        {
            repair.party.SetNavigationModePoint(repair.destination);
            messages.Publish(this, new PartyBehaviorChangeAttempted(repair.party,
                isCurrentlyAtSea: repair.party.IsCurrentlyAtSea));
            Logger.Information("Prepared stale NPC navigation for freeze epoch {Epoch} party {Party} destination {Destination}",
                lease.Epoch, repair.party.StringId, repair.destination);
        }
        foreach (var party in parties)
            if (!snapshot.TryCreateAuthorityState(party, parties, settlements, out _)) return Reject(party, lease);
        return true;
    }

    private static bool Targets(IInteractablePoint interactable, MobileParty target) =>
        (interactable is AnchorPoint anchor && ReferenceEquals(anchor.Owner, target)) ||
        (interactable is PartyBase party && ReferenceEquals(party.MobileParty, target));

    private static bool Finite(CampaignVec2 value) => !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
        !float.IsNaN(value.Y) && !float.IsInfinity(value.Y);

    private static bool Reject(MobileParty party, AuthorityLease lease)
    {
        Logger.Warning("Pre-freeze navigation rejected party {Party} at epoch {Epoch}; no invalid state can be acknowledged",
            party.StringId, lease.Epoch);
        return false;
    }
}
