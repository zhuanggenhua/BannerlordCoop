using Common.Serialization;
using System;
using Xunit;

namespace Common.Tests.Serialization;

public class CampaignSchedulerCheckpointSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripPreservesSchedulingAndEmptyMembership(bool empty)
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var source = new CampaignSchedulerCheckpoint(123, 45, 678,
            new[] { new CampaignPeriodicTickerState("hourly", -1, .75, empty ? Array.Empty<uint>() : new[] { 9u, 8u }) },
            new[] { new CampaignPeriodicEventState(null, 100, 50, 500, true) });
        var received = serializer.Deserialize<CampaignSchedulerCheckpoint>(serializer.Serialize(source));
        Assert.Equal(123, received.LastGameTimeTicks);
        Assert.Equal(45, received.MinimumIntervalTicks);
        Assert.Equal(678, received.CurrentTickCount);
        var ticker = Assert.Single(received.Tickers);
        Assert.Equal("hourly", ticker.Name);
        Assert.Equal(-1, ticker.Index);
        Assert.Equal(.75, ticker.TickDebt);
        Assert.Equal(source.Tickers[0].OrderedHandles, ticker.OrderedHandles);
        var periodicEvent = Assert.Single(received.Events);
        Assert.Null(periodicEvent.Description);
        Assert.Equal(100, periodicEvent.TriggerPeriodTicks);
        Assert.Equal(50, periodicEvent.InitialWaitTicks);
        Assert.Equal(500, periodicEvent.NextTriggerTicks);
        Assert.True(periodicEvent.IsDeleted);
    }
}
