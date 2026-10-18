using ProtoBuf;

namespace GameInterface.Services.Inventory.Data;

[ProtoContract(SkipConstructor = true)]
internal readonly struct EquipmentSlotData
{
    [ProtoMember(1)] public readonly bool HasItem;
    [ProtoMember(2)] public readonly ItemObjectData ItemObjectData;

    public EquipmentSlotData(ItemObjectData itemObjectData)
    {
        HasItem = true;
        ItemObjectData = itemObjectData;
    }
}
