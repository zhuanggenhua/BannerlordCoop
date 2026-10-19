using Common.Messaging;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.MobileParties.Data;
using ProtoBuf;
using System;

namespace Coop.Core.Server.Services.MobileParties.Messages;

/// <summary>Authoritative XP for one regular troop in a joining player's party.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct TroopXpBaselineEntry
{
    [ProtoMember(1)]
    public readonly uint CharacterId;

    [ProtoMember(2)]
    public readonly int Xp;

    public TroopXpBaselineEntry(uint characterId, int xp)
    {
        CharacterId = characterId;
        Xp = xp;
    }
}

/// <summary>Regular-troop XP for one member or prisoner roster.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct TroopRosterXpBaseline
{
    [ProtoMember(1)]
    public readonly uint RosterId;

    [ProtoMember(2)]
    public readonly TroopXpBaselineEntry[] Entries;

    public TroopRosterXpBaseline(uint rosterId, TroopXpBaselineEntry[] entries)
    {
        RosterId = rosterId;
        Entries = entries;
    }
}

/// <summary>
/// Authoritative time and mobile-party state baseline for a joining client.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkJoinCampaignBaseline : IMessage
{
    [ProtoMember(1)]
    public readonly long ServerTicks;

    [ProtoMember(2)]
    public readonly NetworkMobilePartyJoinState[] PartyStates;

    [ProtoMember(3)]
    public readonly bool IsComplete;

    [ProtoMember(4)]
    public readonly TimeControlEnum TimeControlMode;

    [ProtoMember(5)]
    public readonly TroopRosterXpBaseline[] TroopXpBaselines;

    [ProtoMember(6)]
    public readonly bool HasCommittedPausedCheckpointLease;

    [ProtoMember(7)]
    public readonly string PausedCheckpointRoomId;

    [ProtoMember(8)]
    public readonly string PausedCheckpointAuthorityId;

    [ProtoMember(9)]
    public readonly string PausedCheckpointAuthorityControllerId;

    [ProtoMember(10)]
    public readonly long PausedCheckpointEpoch;

    [ProtoMember(11)]
    public readonly string BaselineRequestId;

    public NetworkJoinCampaignBaseline(
        long serverTicks,
        TimeControlEnum timeControlMode,
        NetworkMobilePartyJoinState[] partyStates,
        bool isComplete = true,
        TroopRosterXpBaseline[] troopXpBaselines = null,
        AuthorityLease? committedPausedCheckpointLease = null,
        string baselineRequestId = null)
    {
        ServerTicks = serverTicks;
        TimeControlMode = timeControlMode;
        PartyStates = partyStates;
        IsComplete = isComplete;
        TroopXpBaselines = troopXpBaselines ?? Array.Empty<TroopRosterXpBaseline>();
        BaselineRequestId = baselineRequestId ?? string.Empty;

        AuthorityLease lease = committedPausedCheckpointLease.GetValueOrDefault();
        HasCommittedPausedCheckpointLease = isComplete &&
            HasValidBaselineRequestId(BaselineRequestId) &&
            committedPausedCheckpointLease.HasValue && lease.IsValid && lease.IsPaused;
        PausedCheckpointRoomId = HasCommittedPausedCheckpointLease ? lease.RoomId : string.Empty;
        PausedCheckpointAuthorityId = HasCommittedPausedCheckpointLease ? lease.AuthorityId : string.Empty;
        PausedCheckpointAuthorityControllerId = HasCommittedPausedCheckpointLease
            ? lease.AuthorityControllerId
            : string.Empty;
        PausedCheckpointEpoch = HasCommittedPausedCheckpointLease ? lease.Epoch : 0;
    }

    public bool TryGetCommittedPausedCheckpointLease(out AuthorityLease lease)
    {
        lease = default;
        return IsComplete && TryGetPausedCheckpointLease(out lease);
    }

    public bool TryGetPausedCheckpointLease(out AuthorityLease lease)
    {
        lease = default;
        if (!HasCommittedPausedCheckpointLease) return false;

        var candidate = new AuthorityLease(
            PausedCheckpointRoomId,
            PausedCheckpointAuthorityId,
            PausedCheckpointAuthorityControllerId,
            PausedCheckpointEpoch,
            isPaused: true);
        if (!candidate.IsValid) return false;

        lease = candidate;
        return true;
    }

    public static bool HasValidBaselineRequestId(string baselineRequestId) =>
        Guid.TryParseExact(baselineRequestId, "N", out _);
}
