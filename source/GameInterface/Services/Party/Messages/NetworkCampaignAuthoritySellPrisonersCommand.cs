using Common;
using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Party.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCampaignAuthoritySellPrisonersCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly SellPrisoners Request;

    public NetworkCampaignAuthoritySellPrisonersCommand(
        AuthorityLease lease,
        long sequence,
        SellPrisoners request)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = request.OriginControllerId ?? string.Empty;
        Request = request;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) &&
        OriginControllerId == Request.OriginControllerId && Request.MatchesLease(lease);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCampaignAuthoritySellPrisonersResult : ICommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly string OriginControllerId;
    [ProtoMember(6)] public readonly string RequestStreamId;
    [ProtoMember(7)] public readonly long RequestSequence;
    [ProtoMember(8)] public readonly uint SellingPartyId;
    [ProtoMember(9)] public readonly bool Applied;
    [ProtoMember(10)] public readonly string Reason;

    public NetworkCampaignAuthoritySellPrisonersResult(
        AuthorityLease lease,
        SellPrisoners request,
        bool applied,
        string reason)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        OriginControllerId = request.OriginControllerId ?? string.Empty;
        RequestStreamId = request.RequestStreamId ?? string.Empty;
        RequestSequence = request.RequestSequence;
        SellingPartyId = request.SellingPartyId;
        Applied = applied;
        Reason = reason ?? string.Empty;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && RequestSequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) &&
        !string.IsNullOrWhiteSpace(RequestStreamId);
}
