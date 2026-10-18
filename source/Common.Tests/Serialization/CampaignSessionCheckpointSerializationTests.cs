using Common.Serialization;
using Xunit;

namespace Common.Tests.Serialization;

public class CampaignSessionCheckpointSerializationTests
{
    [Fact]
    public void RoundTripPreservesCampaignAndDetachedSessionJson()
    {
        var checkpoint = new CampaignSessionCheckpoint("campaign", "{\"Players\":[]}");
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var received = serializer.Deserialize<CampaignSessionCheckpoint>(serializer.Serialize(checkpoint));
        Assert.Equal(checkpoint.CampaignId, received.CampaignId);
        Assert.Equal(checkpoint.SessionJson, received.SessionJson);
    }
}
