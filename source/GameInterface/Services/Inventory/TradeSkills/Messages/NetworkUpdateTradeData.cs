using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.Inventory.Data;

namespace GameInterface.Services.Inventory.TradeSkills.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkUpdateTradeData : ICommand
{
    [ProtoMember(1)]
    public readonly string PlayerHeroId;

    [ProtoMember(2)]
    public readonly TradeItemData[] PurchasedItems;

    [ProtoMember(3)]
    public readonly TradeItemData[] SoldItems;

    [ProtoMember(4)]
    public readonly bool IsTrading;

    public NetworkUpdateTradeData(
        string playerHeroId,
        TradeItemData[] purchasedItems,
        TradeItemData[] soldItems,
        bool isTrading)
    {
        PlayerHeroId = playerHeroId;
        PurchasedItems = purchasedItems;
        SoldItems = soldItems;
        IsTrading = isTrading;
    }
}
