using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobilePartyAIs;
using GameInterface.Services.ObjectManager;
using static GameInterface.Services.ObjectManager.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Handlers;

/// <summary>
/// Handles synchronization of the <see cref="MobilePartyAi"/>'s behavior on the campaign map, which includes
/// target positions and target entities used for updating movement.
/// </summary>
/// <remarks>
/// Important note: <see cref="MobilePartyAi"/> is also present in player-controlled parties, where it is 
/// responsible for pathfinding and movement.
/// </remarks>
/// <seealso cref="AiBehavior"/>
internal class MobilePartyBehaviorHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MobilePartyBehaviorHandler>();

    private readonly Dictionary<string, PartyBehaviorUpdateData> latestPredictions = new Dictionary<string, PartyBehaviorUpdateData>();
    private readonly IMessageBroker messageBroker;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IMobilePartyInterface mobilePartyInterface;
    private readonly IPartyBehaviorWireMapper wireMapper;
    private readonly IObjectManager objectManager;
    private readonly IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot;

    public MobilePartyBehaviorHandler(
        IMessageBroker messageBroker,
        IControllerIdProvider controllerIdProvider,
        IMobilePartyInterface mobilePartyInterface,
        IPartyBehaviorWireMapper wireMapper,
        IObjectManager objectManager,
        IMobilePartyBehaviorSnapshot mobilePartyBehaviorSnapshot)
    {
        this.messageBroker = messageBroker;
        this.controllerIdProvider = controllerIdProvider;
        this.mobilePartyInterface = mobilePartyInterface;
        this.wireMapper = wireMapper;
        this.objectManager = objectManager;
        this.mobilePartyBehaviorSnapshot = mobilePartyBehaviorSnapshot;

        messageBroker.Subscribe<PartyBehaviorChangeAttempted>(Handle_PartyBehaviorChanged);
        messageBroker.Subscribe<UpdatePartyBehavior>(Handle_UpdatePartyBehavior);
        messageBroker.Subscribe<NetworkCampaignAuthorityPartyBehaviorDelta>(Handle_AuthorityPartyBehaviorDelta);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyBehaviorChangeAttempted>(Handle_PartyBehaviorChanged);
        messageBroker.Unsubscribe<UpdatePartyBehavior>(Handle_UpdatePartyBehavior);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityPartyBehaviorDelta>(Handle_AuthorityPartyBehaviorDelta);
    }

    public void Handle_PartyBehaviorChanged(MessagePayload<PartyBehaviorChangeAttempted> obj)
    {
        var party = obj.What.Party;
        bool localCampaignAuthority = CampaignSimulationAuthorityPolicy.CanRunWorldSimulation();
        bool locallyControlled = party.IsControlledByThisInstance();

        if (!localCampaignAuthority && !locallyControlled)
            return;

        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData data))
            return;

        data.ForcePosition = obj.What.ForcePosition;
        data.IsCurrentlyAtSea = obj.What.IsCurrentlyAtSea;
        data.ResetMovementToHold = obj.What.ResetMovementToHold;

        if (!localCampaignAuthority)
        {
            data.OriginControllerId = controllerIdProvider.ControllerId;
            latestPredictions[data.MobilePartyId] = data;
            messageBroker.Publish(this, new ControlledPartyBehaviorUpdated(data));
            return;
        }

        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    internal void Handle_AuthorityPartyBehaviorDelta(
        MessagePayload<NetworkCampaignAuthorityPartyBehaviorDelta> payload)
    {
        if (!wireMapper.TryFromNetwork(payload.What.BehaviorUpdateData, out var data))
        { MessageApplyScope.Fail("Authority party movement wire references could not be resolved."); return; }

        messageBroker.Publish(
            payload.Who,
            new UpdatePartyBehavior(ref data, isAuthorityReplay: true));
    }

    public void Handle_UpdatePartyBehavior(MessagePayload<UpdatePartyBehavior> obj)
    {
        var data = obj.What.BehaviorUpdateData;

        Action apply = () =>
        {
            if (!objectManager.TryGetObjectWithLogging(data.MobilePartyId, out MobileParty party))
            { MessageApplyScope.Fail("Authority party movement is missing party " + data.MobilePartyId); return; }

            bool isSelfEcho = party.IsControlledByThisInstance() &&
                !string.IsNullOrEmpty(data.OriginControllerId) &&
                string.Equals(data.OriginControllerId, controllerIdProvider.ControllerId, StringComparison.Ordinal);

            // Reapply the newest local command if an authoritative update arrived before its echo.
            if (!TrySelectBehaviorUpdate(isSelfEcho, latestPredictions, ref data))
                return;

            List<MobileParty> attachedParties = null;
            if ((ModInformation.IsServer || obj.What.IsAuthorityReplay) && data.ForcePosition)
            {
                attachedParties = ApplyServerForcedPosition(party, data.PartyPosition, data.IsCurrentlyAtSea);
                if (data.ResetMovementToHold)
                {
                    party.SetMoveModeHold();
                    party.ResetNavigationToHold();
                }
            }

            IInteractablePoint interactablePoint = null;
            bool correctObserverPosition = ShouldApplyObserverCorrections();
            // AllowedThread keeps outbound movement patches quiet while the complete snapshot is replayed.
            using (new AllowedThread())
            {
                if ((!ModInformation.IsServer || !data.ResetMovementToHold) &&
                    !mobilePartyBehaviorSnapshot.TryApply(
                        party,
                        data,
                        out interactablePoint))
                { MessageApplyScope.Fail("Authority party movement snapshot could not be applied."); return; }

                if (ModInformation.IsClient && data.ForcePosition && !obj.What.IsAuthorityReplay)
                    ApplyForcedPosition(party, data.PartyPosition, data.IsCurrentlyAtSea);

                if (ModInformation.IsClient && data.ResetMovementToHold && !obj.What.IsAuthorityReplay)
                {
                    party.SetMoveModeHold();
                    party.ResetNavigationToHold();
                }

                if (MobilePartyAiConfig.DEBUG)
                {
                    Logger.Debug(
                        "Setting AI behavior. PartyId: {PartyId}, Behavior: {Behavior}, TargetParty: {TargetParty}, BestTargetPoint: {BestTargetPoint}",
                        data.MobilePartyId,
                        data.NewAiBehavior,
                        interactablePoint,
                        data.BestTargetPoint);
                }

                if (correctObserverPosition)
                {
                    // Moving parties already simulate the replicated target, so an in-flight snapshot is stale.
                    if (!data.ForcePosition && ShouldApplyAuthoritativePosition(
                            isSelfEcho,
                            data.ForcePosition,
                            party.PartyMoveMode == MoveModeType.Hold,
                            party.Position,
                            data.PartyPosition))
                        party.Position = data.PartyPosition;
                }
            }

            if (!obj.What.IsAuthorityReplay && CampaignSimulationAuthorityPolicy.CanRunWorldSimulation())
                PublishAuthoritativeBehavior(party, data);

            if (attachedParties != null && !obj.What.IsAuthorityReplay)
            {
                foreach (var attachedParty in attachedParties)
                    PublishForcedPosition(attachedParty);
            }
        };

        if (obj.What.AlreadyOnGameThread)
            apply();
        else
            GameThread.RunSafe(apply, context: nameof(MobilePartyBehaviorHandler));
    }

    internal static bool ShouldApplyObserverCorrections()
    {
        if (ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority) &&
            authority.TryGetCurrentLease(out var lease) && lease.IsValid)
            return !authority.IsLocalCampaignAuthority;
        return ModInformation.IsClient;
    }

    private static void ApplyForcedPosition(MobileParty party, CampaignVec2 position, bool isCurrentlyAtSea)
    {
        party.Position = position;

        if (party.IsCurrentlyAtSea != isCurrentlyAtSea)
            party.ChangeIsCurrentlyAtSeaCheat();
    }

    private static List<MobileParty> ApplyServerForcedPosition(
        MobileParty party,
        CampaignVec2 position,
        bool isCurrentlyAtSea)
    {
        ApplyForcedPosition(party, position, isCurrentlyAtSea);

        if (party.Army == null)
            return null;

        List<MobileParty> attachedParties = null;
        foreach (var attachedParty in party.Army.LeaderParty.AttachedParties)
        {
            if (attachedParty == party)
                continue;

            attachedParty.Position = position;
            attachedParties ??= new List<MobileParty>();
            attachedParties.Add(attachedParty);
        }

        return attachedParties;
    }

    private void PublishForcedPosition(MobileParty party)
    {
        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData data))
            return;

        data.ForcePosition = true;
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    private void PublishAuthoritativeBehavior(MobileParty party, PartyBehaviorUpdateData request)
    {
        if (!mobilePartyBehaviorSnapshot.TryCreate(
                party,
                out PartyBehaviorUpdateData authoritativeData))
            return;

        authoritativeData.OriginControllerId = request.OriginControllerId;
        authoritativeData.ForcePosition = request.ForcePosition;
        authoritativeData.ResetMovementToHold = request.ResetMovementToHold;
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref authoritativeData));
    }

    internal static bool TrySelectBehaviorUpdate(
        bool isSelfEcho,
        IReadOnlyDictionary<string, PartyBehaviorUpdateData> latestPredictions,
        ref PartyBehaviorUpdateData data)
    {
        if (!isSelfEcho)
            return true;

        var partyId = Compact(data.MobilePartyId, typeof(MobileParty));
        return latestPredictions.TryGetValue(partyId, out data);
    }

    internal static bool ShouldApplyAuthoritativePosition(
        bool isSelfEcho,
        bool forcePosition,
        bool isHolding,
        CampaignVec2 currentPosition,
        CampaignVec2 authoritativePosition)
    {
        return !isSelfEcho &&
            (forcePosition || isHolding || currentPosition.IsOnLand != authoritativePosition.IsOnLand);
    }
}
