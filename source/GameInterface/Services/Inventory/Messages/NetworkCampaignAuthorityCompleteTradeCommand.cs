using Common;
using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Inventory.Messages;

/// <summary>
/// A room-host-validated inventory transaction routed to the current campaign authority.
/// Merchant rosters can remain replica-local; the authority commits the player's shared roster,
/// gold, and equipment changes.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCampaignAuthorityCompleteTradeCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly CompleteTrade Trade;

    public NetworkCampaignAuthorityCompleteTradeCommand(
        AuthorityLease lease,
        long sequence,
        string originControllerId,
        CompleteTrade trade)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = originControllerId ?? string.Empty;
        Trade = trade;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) &&
        Trade.RequestId > 0 && (!Trade.IsManagingWarehouse || Trade.IsTrading) &&
        !string.IsNullOrWhiteSpace(Trade.HeroId) &&
        !string.IsNullOrWhiteSpace(Trade.OwnerPartyId);
}
