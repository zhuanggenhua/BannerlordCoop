using Common;
using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Party.Messages;

/// <summary>
/// A room-host-validated party-screen transaction forwarded to the current campaign authority.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCampaignAuthorityPartyDoneLogicCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly NetworkCompleteDoneLogic Request;

    public NetworkCampaignAuthorityPartyDoneLogicCommand(
        AuthorityLease lease,
        long sequence,
        string originControllerId,
        NetworkCompleteDoneLogic request)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = originControllerId ?? string.Empty;
        Request = request;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) && Request.RequestId > 0 &&
        Request.MatchesAuthorityLease(lease) &&
        !string.IsNullOrWhiteSpace(Request.MainHeroId);
}
