using Common;
using GameInterface.Services.CampaignAuthority;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public sealed class CampaignCycleDiagnosticsTests
{
    [Fact]
    public void SemanticCaptureReadsNativeXpWithoutChangingCachedRosterOrVersion()
    {
        var troop = new CharacterObject { StringId = "recruit" };
        var roster = new TroopRoster();
        roster.AddToCounts(troop, 4, xpChange: 100);
        var cached = roster.GetTroopRoster();
        int version = roster.VersionNo;
        roster.SetElementXp(0, 128);
        var captured = CampaignSemanticStateCommand.CaptureStacks(roster);
        Assert.Equal(JsonConvert.SerializeObject(new[] { new { id = "recruit", Number = 4, WoundedNumber = 0, Xp = 128 } }),
            JsonConvert.SerializeObject(captured));
        Assert.Equal(100, cached[0].Xp);
        Assert.Equal(version, roster.VersionNo);
    }

    [Fact]
    public void RepeatedPartyDayRemainsVisibleAcrossEpochsAndNodes()
    {
        var collector = new CampaignCycleDiagnostics();
        var a = new AuthorityLease("room", "world-a", "a", 3);
        var b = new AuthorityLease("room", "world-b", "b", 5);
        Assert.True(collector.Record("DailyTickParty", "lord_2_4", 49, "a", true, true, a));
        Assert.True(collector.Record("DailyTickParty", "lord_2_4", 50, "a", true, true, a));
        Assert.True(collector.Record("DailyTickParty", "lord_2_4", 51, "b", true, true, b));
        var rows = collector.Snapshot();
        Assert.Equal(2, rows.Length);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal(1, rows[1].Count);
        Assert.All(rows, row => Assert.Equal(2, row.Day));
    }

    [Fact]
    public void ObserverAndUnknownLeaseDispatchesAreNotHidden()
    {
        var collector = new CampaignCycleDiagnostics();
        collector.Record("DailyTick", null, 48, "observer", false, false, default);
        var row = Assert.Single(collector.Snapshot());
        Assert.False(row.LocalWriter);
        Assert.False(row.LeaseKnown);
        Assert.Equal("observer", row.Node);
    }

    [Fact]
    public void HourlyEventsUseSeparateHoursAndSnapshotsCannotMutateCollector()
    {
        var collector = new CampaignCycleDiagnostics();
        collector.Record("HourlyTick", null, 48, "a", true, true, default);
        collector.Record("HourlyTick", null, 49, "a", true, true, default);
        collector.Snapshot()[0].Count = 99;
        Assert.All(collector.Snapshot(), row => Assert.Equal(1, row.Count));
    }

    [Fact]
    public void BoundedCollectorFailsExplicitlyInsteadOfDroppingEvidenceQuietly()
    {
        var collector = new CampaignCycleDiagnostics(1);
        collector.Record("DailyTickParty", "one", 24, "a", true, true, default);
        Assert.False(collector.Record("DailyTickParty", "two", 24, "a", true, true, default));
        Assert.True(collector.Overflowed);
        Assert.Single(collector.Snapshot());
        Assert.NotEqual(collector.InstanceId, new CampaignCycleDiagnostics().InstanceId);
    }
}
