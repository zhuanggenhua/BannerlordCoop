using Common;
using Common.Util;
using GameInterface.Services.Kingdoms.Data;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;

namespace GameInterface.Services.CampaignAuthority.NativeState;

internal sealed partial class CampaignNativeBehaviorCheckpointService
{
    [ProtoContract]
    internal sealed class ProposalHistoryPayload
    {
        [ProtoMember(1)] public KingdomDecisionData[] Decisions { get; set; } = Array.Empty<KingdomDecisionData>();
    }

    internal bool TryCaptureKingdomProposals(KingdomDecisionProposalBehavior behavior, out CampaignKingdomProposalState state)
    {
        state = null;
        if (behavior?._kingdomDecisionsList == null || decisionConverter == null)
            return Reject("capture.kingdom.history/converter=null");
        var data = new List<KingdomDecisionData>();
        foreach (var decision in behavior._kingdomDecisionsList)
        {
            if (decision == null) return RejectValue($"capture.kingdom.history[{data.Count}]=null");
            KingdomDecisionData converted;
            try { converted = decisionConverter.Convert(decision); }
            catch (InvalidOperationException ex) { return RejectValue($"capture.kingdom.history[{data.Count}]: {ex.Message}"); }
            if (!TryRestoreHistoryDecision(converted, out _)) return false;
            data.Add(converted);
        }
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new ProposalHistoryPayload { Decisions = data.ToArray() });
        state = new CampaignKingdomProposalState(data.Count, stream.ToArray());
        return true;
    }

    internal bool TryPrepareKingdomProposals(CampaignKingdomProposalState state,
        KingdomDecisionProposalBehavior behavior, out Action install)
    {
        install = null;
        if (state == null || behavior == null || state.Payload == null || state.Count < 0)
            return Reject("prepare.kingdom.history/header=null or invalid");
        ProposalHistoryPayload payload;
        try
        {
            using var stream = new MemoryStream(state.Payload, false);
            payload = Serializer.Deserialize<ProposalHistoryPayload>(stream);
        }
        catch (Exception ex) when (ex is ProtoException || ex is EndOfStreamException || ex is InvalidOperationException)
        { return RejectValue($"prepare.kingdom.payload: {ex.GetType().Name}: {ex.Message}"); }
        if (payload?.Decisions == null || payload.Decisions.Length != state.Count)
            return RejectValue($"prepare.kingdom.count={state.Count}, decoded={payload?.Decisions?.Length}");
        var decisions = new List<KingdomDecision>();
        foreach (var data in payload.Decisions)
        {
            if (!TryRestoreHistoryDecision(data, out var decision)) return false;
            decisions.Add(decision);
        }
        install = () =>
        {
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
                behavior._kingdomDecisionsList = decisions;
        };
        return true;
    }

    private bool TryRestoreHistoryDecision(KingdomDecisionData data, out KingdomDecision decision)
    {
        decision = null;
        if (data == null || data.TriggerTime < 0)
            return RejectValue($"kingdom.history.type={data?.GetType().Name}, ticks={data?.TriggerTime}");
        if (string.IsNullOrEmpty(data.ProposerClanId) || string.IsNullOrEmpty(data.KingdomId) ||
            !objects.TryGetObject<Clan>(data.ProposerClanId, out var proposer) ||
            !objects.TryGetObject<Kingdom>(data.KingdomId, out var kingdom))
            return RejectValue($"kingdom.history.baseRefs clan={data.ProposerClanId}, kingdom={data.KingdomId}");
        if (data is AcceptCallToWarAgreementDecisionData accept)
        {
            if (string.IsNullOrEmpty(accept.CallingKingdomId) || string.IsNullOrEmpty(accept.KingdomToCallToWarAgainstId) ||
                !objects.TryGetObject<Kingdom>(accept.CallingKingdomId, out var calling) ||
                !objects.TryGetObject<Kingdom>(accept.KingdomToCallToWarAgainstId, out var target))
                return RejectValue($"kingdom.history.acceptRefs calling={accept.CallingKingdomId}, target={accept.KingdomToCallToWarAgainstId}");
            // History is a value index. The native constructor calculates an AI cost: never invoke it here.
            var restored = ObjectHelper.SkipConstructor<AcceptCallToWarAgreementDecision>();
            restored.ProposerClan = proposer;
            restored._kingdom = kingdom;
            restored.TriggerTime = new CampaignTime(data.TriggerTime);
            restored.IsEnforced = data.IsEnforced;
            restored.NotifyPlayer = data.NotifyPlayer;
            restored.PlayerExamined = data.PlayerExamined;
            SetAcceptValue(restored, nameof(AcceptCallToWarAgreementDecision.CallingKingdom), calling);
            SetAcceptValue(restored, nameof(AcceptCallToWarAgreementDecision.KingdomToCallToWarAgainst), target);
            SetAcceptValue(restored, nameof(AcceptCallToWarAgreementDecision.CallToWarCost), accept.CallToWarCost);
            decision = restored;
            return true;
        }
        if (!data.TryGetKingdomDecision(objects, out decision))
            return RejectValue($"kingdom.history.refs type={data.GetType().Name}, clan={data.ProposerClanId}, kingdom={data.KingdomId}");
        return true;
    }

    private static void SetAcceptValue(AcceptCallToWarAgreementDecision decision, string field, object value) =>
        typeof(AcceptCallToWarAgreementDecision).GetField(field, BindingFlags.Public | BindingFlags.Instance).SetValue(decision, value);
}
