using ProtoBuf;
using System.Collections.Generic;

namespace Common;

/// <summary>Allocation high-water marks, including identities of objects that have already been removed.</summary>
[ProtoContract]
public sealed class RegistryAllocationCheckpoint
{
    // Zero means that the uint handle space is exhausted, not that allocation starts at zero.
    [ProtoMember(1)] public readonly uint NextHandle;
    [ProtoMember(2)] public readonly Dictionary<string, int> TypeCounters;

    private RegistryAllocationCheckpoint()
    {
        TypeCounters = new Dictionary<string, int>();
    }

    public RegistryAllocationCheckpoint(uint nextHandle, IDictionary<string, int> typeCounters)
    {
        NextHandle = nextHandle;
        TypeCounters = new Dictionary<string, int>(typeCounters);
    }
}
