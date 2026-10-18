using ProtoBuf;

namespace Common;

/// <summary>The old writer's final clock, sent reliably after its last world updates.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class CampaignClockCheckpoint
{
    [ProtoMember(1)] public readonly long Ticks;
    public CampaignClockCheckpoint(long ticks) { Ticks = ticks; }
}
