using GameInterface.Services.ObjectManager;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority.Movement;

public interface ICampaignAiContinuation
{
    bool TryCapture(out CampaignAiContinuationState[] states);
    bool TryPrepare(CampaignAiContinuationState[] states, uint[] partyHandles, out Func<bool> apply);
}

/// <summary>Finite native AI leaf state; navigation and references remain in the movement snapshot.</summary>
public sealed class CampaignAiContinuation : ICampaignAiContinuation
{
    private readonly IObjectManager objects;

    public CampaignAiContinuation(IObjectManager objects) => this.objects = objects;

    public bool TryCapture(out CampaignAiContinuationState[] states)
    {
        states = null;
        var parties = Campaign.Current?.CampaignObjectManager?.MobileParties;
        if (parties == null) return false;
        var result = new List<CampaignAiContinuationState>();
        foreach (var party in parties.Where(p => p?.IsActive == true))
        {
            var ai = party.Ai;
            if (ai?._fleeingData == null || !ReferenceEquals(ai._mobileParty, party) ||
                !objects.TryGetHandle(party, out var partyId) || !objects.TryGetHandle(ai, out var aiId)) return false;
            var state = Read(ai, partyId, aiId);
            if (!Valid(state)) return false;
            result.Add(state);
        }
        states = result.OrderBy(s => s.PartyId).ToArray();
        return true;
    }

    public bool TryPrepare(CampaignAiContinuationState[] states, uint[] partyHandles, out Func<bool> apply)
    {
        apply = null;
        if (states == null || partyHandles == null || states.Length != partyHandles.Length) return false;
        var ais = new MobilePartyAi[states.Length];
        var handles = new HashSet<uint>();
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (!Valid(state) || state.PartyId != partyHandles[i] || !handles.Add(state.AiId) ||
                !objects.TryGetObject<MobileParty>(state.PartyId, out var party) || !party.IsActive ||
                !objects.TryGetObject<MobilePartyAi>(state.AiId, out var ai) ||
                !ReferenceEquals(party.Ai, ai) || !ReferenceEquals(ai._mobileParty, party)) return false;
            ais[i] = ai;
        }
        bool consumed = false;
        apply = () =>
        {
            if (consumed) return false;
            consumed = true;
            // Recheck every binding before the first write, including changes since prepare.
            for (int i = 0; i < states.Length; i++)
                if (!objects.TryGetObject<MobileParty>(states[i].PartyId, out var party) || !party.IsActive ||
                    !objects.TryGetObject<MobilePartyAi>(states[i].AiId, out var ai) ||
                    !ReferenceEquals(ai, ais[i]) || !ReferenceEquals(party.Ai, ai) ||
                    !ReferenceEquals(ai._mobileParty, party)) return false;
            for (int i = 0; i < states.Length; i++) Write(ais[i], states[i]);
            return true;
        };
        return true;
    }

    private static bool Valid(CampaignAiContinuationState state) =>
        state != null && state.PartyId != 0 && state.AiId != 0 &&
        Finite(state.AttackInitiative) && Finite(state.AvoidInitiative);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static CampaignAiContinuationState Read(MobilePartyAi ai, uint party, uint id) => new()
    {
        PartyId = party, AiId = id, HourCounter = ai.HourCounter,
        Reached = ai._fleeingData.AlreadyReachedTheDestinationWhileFleeing,
        Clockwise = ai._fleeingData.ShouldFleeClockWise,
        CwBlocked = ai._fleeingData.CwFleeDirectionIsBlocked,
        CcwBlocked = ai._fleeingData.CcwFleeDirectionIsBlocked,
        Disabled = ai._isDisabled, Rethink = ai.RethinkAtNextHourlyTick,
        NoDecisions = ai.DoNotMakeNewDecisions, Alerted = ai.IsAlerted,
        NeedsUpdate = ai.DefaultBehaviorNeedsUpdate,
        AttackInitiative = ai._attackInitiative, AvoidInitiative = ai._avoidInitiative,
        RestoreTicks = ai._initiativeRestoreTime.NumTicks, NextCheckTicks = ai._nextAiCheckTime.NumTicks,
        EnableTicks = ai._enableAgainAtHour.NumTicks, NoAttackTicks = ai.DoNotAttackMainPartyUntil.NumTicks,
    };

    private static void Write(MobilePartyAi ai, CampaignAiContinuationState state)
    {
        ai._fleeingData = new MobilePartyAi.FleeingData
        {
            AlreadyReachedTheDestinationWhileFleeing = state.Reached,
            ShouldFleeClockWise = state.Clockwise,
            CwFleeDirectionIsBlocked = state.CwBlocked,
            CcwFleeDirectionIsBlocked = state.CcwBlocked,
        };
        ai.HourCounter = state.HourCounter;
        ai._isDisabled = state.Disabled;
        ai.RethinkAtNextHourlyTick = state.Rethink;
        ai.DoNotMakeNewDecisions = state.NoDecisions;
        ai.IsAlerted = state.Alerted;
        ai.DefaultBehaviorNeedsUpdate = state.NeedsUpdate;
        ai._attackInitiative = state.AttackInitiative;
        ai._avoidInitiative = state.AvoidInitiative;
        ai._initiativeRestoreTime = new CampaignTime(state.RestoreTicks);
        ai._nextAiCheckTime = new CampaignTime(state.NextCheckTicks);
        ai._enableAgainAtHour = new CampaignTime(state.EnableTicks);
        ai.DoNotAttackMainPartyUntil = new CampaignTime(state.NoAttackTicks);
    }
}

[ProtoContract]
public sealed class CampaignAiContinuationPayload
{
    [ProtoMember(1)] public int Version { get; set; }
    [ProtoMember(2)] public CampaignAiContinuationState[] States { get; set; } = Array.Empty<CampaignAiContinuationState>();
}

[ProtoContract]
public sealed class CampaignAiContinuationState
{
    [ProtoMember(1)] public uint PartyId { get; set; }
    [ProtoMember(2)] public uint AiId { get; set; }
    [ProtoMember(3)] public int HourCounter { get; set; }
    [ProtoMember(4)] public bool Reached { get; set; }
    [ProtoMember(5)] public bool Clockwise { get; set; }
    [ProtoMember(6)] public bool CwBlocked { get; set; }
    [ProtoMember(7)] public bool CcwBlocked { get; set; }
    [ProtoMember(8)] public bool Disabled { get; set; }
    [ProtoMember(9)] public bool Rethink { get; set; }
    [ProtoMember(10)] public bool NoDecisions { get; set; }
    [ProtoMember(11)] public bool Alerted { get; set; }
    [ProtoMember(12)] public bool NeedsUpdate { get; set; }
    [ProtoMember(13)] public float AttackInitiative { get; set; }
    [ProtoMember(14)] public float AvoidInitiative { get; set; }
    [ProtoMember(15)] public long RestoreTicks { get; set; }
    [ProtoMember(16)] public long NextCheckTicks { get; set; }
    [ProtoMember(17)] public long EnableTicks { get; set; }
    [ProtoMember(18)] public long NoAttackTicks { get; set; }
}
