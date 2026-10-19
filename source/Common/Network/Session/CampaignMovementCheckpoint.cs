using ProtoBuf;
using System;

namespace Common.Network.Session;

/// <summary>Final frozen movement state, bound to the writer that captured it.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class CampaignMovementCheckpoint
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string ControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Ticks;
    [ProtoMember(6)] public readonly int PartyCount;
    [ProtoMember(7)] public readonly byte[] Payload;
    [ProtoMember(8)] public readonly string Sha256;

    public CampaignMovementCheckpoint(AuthorityLease source, long ticks, int partyCount, byte[] payload, string sha256)
    {
        RoomId = source.RoomId;
        AuthorityId = source.AuthorityId;
        ControllerId = source.AuthorityControllerId;
        Epoch = source.Epoch;
        Ticks = ticks;
        PartyCount = partyCount;
        Payload = payload;
        Sha256 = sha256;
    }

    public AuthorityLease SourceLease => new AuthorityLease(RoomId, AuthorityId, ControllerId, Epoch);

    public bool Matches(AuthorityLease source, long ticks) =>
        source.IsValid && !source.IsPaused && SourceLease == source && Ticks == ticks && ticks >= 0;

    public bool Precedes(AuthorityLease paused) =>
        paused.IsValid && paused.IsPaused && Epoch < long.MaxValue && paused.Epoch == Epoch + 1 &&
        string.Equals(RoomId, paused.RoomId, StringComparison.Ordinal) && SourceLease.IsValid;
}
