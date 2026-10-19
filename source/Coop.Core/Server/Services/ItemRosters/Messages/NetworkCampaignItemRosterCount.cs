using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.ItemRosters.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignItemRosterCount : ICampaignAuthorityScopedMessage
{
    [ProtoMember(1)] public readonly uint RosterId;
    [ProtoMember(2)] public readonly uint ItemId;
    [ProtoMember(3)] public readonly uint ModifierId;
    [ProtoMember(4)] public readonly string ItemStringId;
    [ProtoMember(5)] public readonly int Count;
    [ProtoMember(6)] public readonly bool Clear;

    public NetworkCampaignItemRosterCount(uint rosterId, uint itemId, uint modifierId,
        string itemStringId, int count, bool clear = false)
    {
        RosterId = rosterId;
        ItemId = itemId;
        ModifierId = modifierId;
        ItemStringId = itemStringId;
        Count = count;
        Clear = clear;
    }
}
