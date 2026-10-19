using Common;
using GameInterface.Services.CampaignAuthority;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;
using HarmonyLib;

namespace GameInterface.Tests.Services.CampaignAuthority;

public sealed class CampaignCycleDiagnosticsTests
{
    [Fact]
    public void NativeFrameDuplicateCounterIsCampaignScopedAndIndependentOfSessionMode()
    {
        var collector = new CampaignCycleDiagnostics();
        collector.RealTickFrame(100); collector.RealTickFrame(101); collector.RealTickFrame(101);
        Assert.Equal(1, collector.SimulationSnapshot()["Campaign.RealTick.sameFrameDuplicate.failed"]);
        var other = new CampaignCycleDiagnostics();
        other.RealTickFrame(101);
        Assert.Empty(other.SimulationSnapshot());
    }

    [Fact]
    public void FailedNativeTickerPreservesOriginalExceptionWithoutReadingBrokenInstance()
    {
        var original = new System.NullReferenceException("native list missing");
        var collector = new CampaignCycleDiagnostics();
        var batch = new CampaignTickerEvidencePatches.TickerBatch { Instance = new object(), Collector = collector,
            Audit = new CampaignTickerAudit { Ticker = "broken", Node = "a" } };
        var result = AccessTools.Method(typeof(CampaignTickerEvidencePatches), "Finalizer").Invoke(null, new object[] { original, batch });
        Assert.Same(original, result);
        var audit = Assert.Single(collector.AuditSnapshot());
        Assert.Equal(1, audit.Mismatches);
        Assert.Contains("native list missing", audit.FirstFailure);
    }

    [Fact]
    public void ReadOnlyEvidencePatchesBindInstalledNativeOverloads()
    {
        var harmony = new Harmony("coop.tests.native-cycle-evidence");
        try
        {
            foreach (var type in new[] { typeof(CampaignTickerEvidencePatches), typeof(CampaignTickerCallbackEvidencePatches),
                typeof(CampaignMobilePartyCallbackEvidencePatches), typeof(CampaignSimulationEvidencePatches),
                typeof(CampaignEconomicEvidencePatches), typeof(CampaignWageEvidencePatches) })
                Assert.NotEmpty(harmony.CreateClassProcessor(type).Patch());
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    [Fact]
    public void NativeDueSlotsDistinguishCatchupWithoutHidingRepeatedSameSlot()
    {
        var collector = new CampaignCycleDiagnostics();
        collector.Record("HourlyTick", null, 50, "a", true, true, default, 100);
        collector.Record("HourlyTick", null, 50, "a", true, true, default, 200);
        collector.Record("HourlyTick", null, 51, "a", true, true, default, 200);
        var rows = collector.Snapshot();
        Assert.Equal(2, rows.Length);
        Assert.Equal(100, rows[0].DueTicks);
        Assert.Equal(1, rows[0].Count);
        Assert.Equal(200, rows[1].DueTicks);
        Assert.Equal(2, rows[1].Count);
    }

    [Fact]
    public void NativeTickerPredictionPreservesStrictDebtBoundaryAndCatchupOrder()
    {
        var ids = new[] { "first", "second", "third" };
        Assert.Empty(CampaignTickerEvidencePatches.Expected(ids, -1, 1, 0, out int index, out double debt));
        Assert.Equal(-1, index); Assert.Equal(1, debt);
        Assert.Equal(new[] { "third", "first", "second", "third", "first", "second" },
            CampaignTickerEvidencePatches.Expected(ids, 1, 0.5, 2, out index, out debt));
        Assert.Equal(1, index); Assert.Equal(0.5, debt);
        Assert.Empty(CampaignTickerEvidencePatches.Expected(System.Array.Empty<string>(), 7, 0.9, 2, out index, out debt));
        Assert.Equal(7, index); Assert.Equal(0, debt);
    }

    [Fact]
    public void AuditMergesCountsAndHashesWithoutDiscardingFirstFailure()
    {
        var collector = new CampaignCycleDiagnostics();
        var first = new CampaignTickerAudit { Ticker = "daily", Node = "a", Epoch = 3,
            Expected = 2, Completed = 2, ExpectedHash = "same", CompletedHash = "same" };
        collector.Audit(first);
        collector.Audit(new CampaignTickerAudit { Ticker = "daily", Node = "a", Epoch = 3,
            Expected = 2, Completed = 1, ExpectedHash = "different", CompletedHash = "missing", Mismatches = 1, FirstFailure = "missing id" });
        var row = Assert.Single(collector.AuditSnapshot());
        Assert.Equal(2, row.Batches); Assert.Equal(4, row.Expected); Assert.Equal(3, row.Completed);
        Assert.Equal(1, row.Mismatches); Assert.Equal("missing id", row.FirstFailure);
        Assert.NotEqual(row.ExpectedHash, row.CompletedHash);
        row.Completed = 99;
        Assert.Equal(3, Assert.Single(collector.AuditSnapshot()).Completed);
    }

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
