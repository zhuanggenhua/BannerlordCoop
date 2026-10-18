using ProtoBuf;
using static TaleWorlds.Core.Equipment;

namespace GameInterface.Services.Inventory.Data;

[ProtoContract(SkipConstructor = true)]
internal readonly struct EquipmentData
{
    [ProtoMember(1)]
    public readonly EquipmentType EquipmentType;

    [ProtoMember(2)]
    public readonly EquipmentSlotData[] ItemSlots;

    public EquipmentData(
        EquipmentType equipmentType,
        EquipmentSlotData[] itemSlots)
    {
        EquipmentType = equipmentType;
        ItemSlots = itemSlots;
    }
}
