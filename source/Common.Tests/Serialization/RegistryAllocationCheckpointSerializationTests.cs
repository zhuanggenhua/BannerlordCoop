using Common.Serialization;
using System.Collections.Generic;
using Xunit;

namespace Common.Tests.Serialization;

public class RegistryAllocationCheckpointSerializationTests
{
    [Theory]
    [InlineData(1u, false)]
    [InlineData(100u, true)]
    [InlineData(0u, true)]
    public void RoundTripPreservesHighWaterMarksAndEmptyCounters(uint nextHandle, bool hasCounters)
    {
        var counters = new Dictionary<string, int>();
        if (hasCounters) counters.Add("campaign-type, game-assembly", 84);
        var checkpoint = new RegistryAllocationCheckpoint(nextHandle, counters);
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var received = serializer.Deserialize<RegistryAllocationCheckpoint>(serializer.Serialize(checkpoint));
        Assert.Equal(nextHandle, received.NextHandle);
        Assert.NotNull(received.TypeCounters);
        Assert.Equal(counters, received.TypeCounters);
    }
}
