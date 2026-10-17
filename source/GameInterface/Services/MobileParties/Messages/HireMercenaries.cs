using Common.Messaging;
using ProtoBuf;

using Common;

namespace GameInterface.Services.MobileParties.Messages;

/// <summary>
/// [Client -> Room Host] Requests a tavern mercenary hire. The host validates player ownership and
/// routes the request to the active campaign authority, which checks its current recruitment pool
/// and hero gold before applying the hire. HeroGold is the requester's snapshot for reject diagnostics.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct HireMercenaries : ICommand
{
    [ProtoMember(1)]
    public readonly string MainHeroId;
    [ProtoMember(2)]
    public readonly string MainPartyId;
    [ProtoMember(3)]
    public readonly string TownId;
    [ProtoMember(4)]
    public readonly string MercenaryTroopId;
    [ProtoMember(5)]
    public readonly int Count;
    [ProtoMember(6)]
    public readonly int GoldAmount;
    [ProtoMember(7)]
    public readonly int HeroGold;
    [ProtoMember(8)]
    public readonly long RequestSequence;
    [ProtoMember(9)]
    public readonly string RoomId;
    [ProtoMember(10)]
    public readonly string AuthorityId;
    [ProtoMember(11)]
    public readonly string AuthorityControllerId;
    [ProtoMember(12)]
    public readonly long Epoch;

    public HireMercenaries(
        string mainHeroId,
        string mainPartyId,
        string townId,
        string mercenaryTroopId,
        int count,
        int goldAmount,
        int heroGold,
        long requestSequence = 0,
        AuthorityLease lease = default)
    {
        MainHeroId = mainHeroId;
        MainPartyId = mainPartyId;
        TownId = townId;
        MercenaryTroopId = mercenaryTroopId;
        Count = count;
        GoldAmount = goldAmount;
        HeroGold = heroGold;
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
