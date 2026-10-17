using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.TroopRosters.Messages;

/// <summary>
/// A player recruitment request that the room host validated and forwarded to the active
/// campaign authority. The lease and sequence fence commands delayed or replayed across handoff.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityRecruitmentCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly string MobilePartyId;
    [ProtoMember(8)] public readonly TroopInfo[] TroopsInCart;

    public NetworkCampaignAuthorityRecruitmentCommand(
        AuthorityLease lease,
        long sequence,
        string originControllerId,
        string mobilePartyId,
        TroopInfo[] troopsInCart)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = originControllerId ?? string.Empty;
        MobilePartyId = mobilePartyId ?? string.Empty;
        TroopsInCart = troopsInCart ?? System.Array.Empty<TroopInfo>();
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) &&
        !string.IsNullOrWhiteSpace(MobilePartyId) && TroopsInCart != null;
}
