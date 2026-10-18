using ProtoBuf;

namespace GameInterface.Services.Inventory.Data;

[ProtoContract(SkipConstructor = true)]
internal readonly struct TradeItemData
{
    [ProtoMember(1)] public readonly ItemRosterElementData Element;
    [ProtoMember(2)] public readonly int TotalPrice;

    public TradeItemData(ItemRosterElementData element, int totalPrice)
    {
        Element = element;
        TotalPrice = totalPrice;
    }
}
