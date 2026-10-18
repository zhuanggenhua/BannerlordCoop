using Common.Messaging;
using Common;
using GameInterface.Services.Inventory.Data;
using ProtoBuf;
using System.Collections.Generic;
using System.Threading;

namespace GameInterface.Services.Inventory.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct CompleteTrade : ICommand
{
    private static long nextRequestId;
    [ProtoMember(21)]
    public readonly long RequestId;
    [ProtoMember(22)]
    public readonly string RoomId;
    [ProtoMember(23)]
    public readonly string AuthorityId;
    [ProtoMember(24)]
    public readonly string AuthorityControllerId;
    [ProtoMember(25)]
    public readonly long Epoch;
    [ProtoMember(1)]
    public readonly string FromItemRosterId;
    [ProtoMember(2)]
    public readonly bool IsFromItemRosterNull;
    [ProtoMember(3)]
    public readonly string ToItemRosterId;
    [ProtoMember(4)]
    public readonly ItemRosterElementData[] FromItemRosterData;
    [ProtoMember(5)]
    public readonly ItemRosterElementData[] ToItemRosterData;
    [ProtoMember(6)]
    public readonly Dictionary<string, EquipmentData[]> CharacterIdEquipmentsData;
    [ProtoMember(7)]
    public readonly bool IsTrading;
    [ProtoMember(8)]
    public readonly bool CanGainXpFromDiscarding;
    [ProtoMember(9)]
    public readonly bool IsManagingWarehouse;
    [ProtoMember(10)]
    public readonly string HeroId;
    [ProtoMember(11)]
    public readonly string InitialHeroId;
    [ProtoMember(12)]
    public readonly int TotalAmount;
    [ProtoMember(13)]
    public readonly int MerchantGold;
    [ProtoMember(14)]
    public readonly string OwnerPartyId;
    [ProtoMember(15)]
    public readonly string CurrentMobilePartyId;
    [ProtoMember(16)]
    public readonly bool IsSettlementComponentNull;
    [ProtoMember(17)]
    public readonly string CurrentSettlementComponentId;

    [ProtoMember(18)]
    public readonly TradeItemData[] BoughtItems;
    [ProtoMember(19)]
    public readonly TradeItemData[] SoldItems;
    [ProtoMember(20)]
    public readonly string ForceTransferId;

    public CompleteTrade(
        string fromItemRosterId,
        bool isFromItemRosterNull,
        string toItemRosterId,
        ItemRosterElementData[] fromItemRosterData,
        ItemRosterElementData[] toItemRosterData,
        Dictionary<string, EquipmentData[]> characterIdEquipmentsData,
        bool isTrading,
        bool canGainXpFromDiscarding,
        bool isManagingWarehouse,
        string heroId,
        string initialHeroId,
        int totalAmount,
        int merchantGold,
        string ownerPartyId,
        string currentMobilePartyId,
        bool isSettlementComponentNull,
        string currentSettlementComponentId,
        TradeItemData[] boughtItems,
        TradeItemData[] soldItems,
        string forceTransferId = null,
        AuthorityLease authorityLease = default)
    {
        RequestId = Interlocked.Increment(ref nextRequestId);
        FromItemRosterId = fromItemRosterId;
        IsFromItemRosterNull = isFromItemRosterNull;
        ToItemRosterId = toItemRosterId;
        // The local authority does not serialize this request before the UI resets its preview.
        FromItemRosterData = fromItemRosterData == null ? null : (ItemRosterElementData[])fromItemRosterData.Clone();
        ToItemRosterData = toItemRosterData == null ? null : (ItemRosterElementData[])toItemRosterData.Clone();
        CharacterIdEquipmentsData = characterIdEquipmentsData;
        IsTrading = isTrading;
        CanGainXpFromDiscarding = canGainXpFromDiscarding;
        IsManagingWarehouse = isManagingWarehouse;
        HeroId = heroId;
        InitialHeroId = initialHeroId;
        TotalAmount = totalAmount;
        MerchantGold = merchantGold;
        OwnerPartyId = ownerPartyId;
        CurrentMobilePartyId = currentMobilePartyId;
        IsSettlementComponentNull = isSettlementComponentNull;
        CurrentSettlementComponentId = currentSettlementComponentId;
        BoughtItems = boughtItems;
        SoldItems = soldItems;
        ForceTransferId = forceTransferId;
        RoomId = authorityLease.RoomId;
        AuthorityId = authorityLease.AuthorityId;
        AuthorityControllerId = authorityLease.AuthorityControllerId;
        Epoch = authorityLease.Epoch;
    }

    public bool MatchesAuthorityLease(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch;
}
