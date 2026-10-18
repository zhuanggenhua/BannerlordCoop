using Common.Serialization;
using Xunit;

namespace Common.Tests.Serialization;

public class CampaignClockCheckpointSerializationTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(768000000000L)]
    public void RoundTripPreservesExactTicksIncludingZero(long ticks)
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var received = serializer.Deserialize<CampaignClockCheckpoint>(serializer.Serialize(new CampaignClockCheckpoint(ticks)));
        Assert.NotNull(received);
        Assert.Equal(ticks, received.Ticks);
    }
}
