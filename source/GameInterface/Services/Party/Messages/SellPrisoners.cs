using Common.Messaging;
using Common;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;
using System;

namespace GameInterface.Services.Party.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct SellPrisoners : ICommand
{
    [ProtoMember(1)]
    public readonly uint SellingPartyId;

    [ProtoMember(2)]
    public readonly TroopRosterData LeftPrisonerRosterData;

    [ProtoMember(3)]
    public readonly string RoomId;

    [ProtoMember(4)]
    public readonly string AuthorityId;

    [ProtoMember(5)]
    public readonly string AuthorityControllerId;

    [ProtoMember(6)]
    public readonly long Epoch;

    [ProtoMember(7)]
    public readonly string OriginControllerId;

    [ProtoMember(8)]
    public readonly string RequestStreamId;

    [ProtoMember(9)]
    public readonly long RequestSequence;

    public SellPrisoners(
        uint sellingPartyId,
        TroopRosterData leftPrisonerRosterData)
    {
        SellingPartyId = sellingPartyId;
        LeftPrisonerRosterData = leftPrisonerRosterData;
        RoomId = string.Empty;
        AuthorityId = string.Empty;
        AuthorityControllerId = string.Empty;
        Epoch = 0;
        OriginControllerId = string.Empty;
        RequestStreamId = string.Empty;
        RequestSequence = 0;
    }

    public SellPrisoners(
        uint sellingPartyId,
        TroopRosterData leftPrisonerRosterData,
        AuthorityLease lease,
        string originControllerId,
        string requestStreamId,
        long requestSequence)
    {
        SellingPartyId = sellingPartyId;
        LeftPrisonerRosterData = leftPrisonerRosterData;
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        OriginControllerId = originControllerId ?? string.Empty;
        RequestStreamId = requestStreamId ?? string.Empty;
        RequestSequence = requestSequence;
    }

    public bool IsLegacy => string.IsNullOrWhiteSpace(RoomId) &&
        string.IsNullOrWhiteSpace(AuthorityId) &&
        string.IsNullOrWhiteSpace(AuthorityControllerId) && Epoch == 0 &&
        string.IsNullOrWhiteSpace(OriginControllerId) &&
        string.IsNullOrWhiteSpace(RequestStreamId) && RequestSequence == 0;

    public bool MatchesLease(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && RequestSequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) &&
        Guid.TryParseExact(RequestStreamId, "N", out _);
}
