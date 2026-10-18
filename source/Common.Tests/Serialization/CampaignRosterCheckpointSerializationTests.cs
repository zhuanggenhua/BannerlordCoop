using Common.Serialization;
using System;
using Xunit;

namespace Common.Tests.Serialization;

public class CampaignRosterCheckpointSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripKeepsEmptyRostersAndRepeatedCharacterStacks(bool empty)
    {
        var state = new CampaignRosterState(42, empty ? Array.Empty<CampaignRosterElementState>() : new[]
        {
            new CampaignRosterElementState(20, 3, 1, 221),
            new CampaignRosterElementState(20, 3, 1, 242),
        });
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var received = serializer.Deserialize<CampaignRosterCheckpoint>(serializer.Serialize(new CampaignRosterCheckpoint(new[] { state })));
        var roster = Assert.Single(received.Rosters);
        Assert.Equal(42u, roster.RosterHandle);
        Assert.Equal(state.Elements, roster.Elements);
    }
}
