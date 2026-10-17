using Common.Messaging;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.CampaignAuthority;
using ProtoBuf;

namespace Coop.Core.Client.Services.TroopRosters.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct ClientRequestRecruitment : IEvent
{
    [ProtoMember(1)]
    public string MobilePartyId { get; }
    [ProtoMember(2)]
    public TroopInfo[] TroopsInCart { get; }
    [ProtoMember(3)]
    public long RequestSequence { get; }
    [ProtoMember(4)]
    public string RoomId { get; }
    [ProtoMember(5)]
    public string AuthorityId { get; }
    [ProtoMember(6)]
    public string AuthorityControllerId { get; }
    [ProtoMember(7)]
    public long Epoch { get; }

    public ClientRequestRecruitment(
        string mobilePartyId,
        TroopInfo[] troopsInCart,
        long requestSequence = 0,
        AuthorityLease lease = default)
    {
        MobilePartyId = mobilePartyId;
        TroopsInCart = troopsInCart;
        RequestSequence = requestSequence;
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
    }

    public bool MatchesLease(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && RequestSequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch;
}
