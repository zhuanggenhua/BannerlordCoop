using Common;
using GameInterface.Services.CampaignAuthority;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;
using TaleWorlds.ObjectSystem;
using System;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.Library;
using System.Reflection;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignCycleDiagnosticsTests
{
    [Fact]
    public void EveryTargetIsAnActualClosedNativeTickerBoundary()
    {
        var targets = CampaignTickerEvidencePatches.TargetMethods().ToArray();
        Assert.NotEmpty(targets);
        Assert.All(targets, method =>
        {
            Assert.Equal("PeriodicTickSome", method.Name);
            Assert.False(method.ContainsGenericParameters);
            Assert.True(method.DeclaringType!.IsConstructedGenericType);
            Assert.Equal(typeof(double), Assert.Single(method.GetParameters()).ParameterType);
        });
        var expected = new[] { "MobilePartyHourlyTick", "PeriodicHourlyTick", "PeriodicDailyTick", "PeriodicQuarterDailyTick", "TickPartialHourlyAi" }
            .SelectMany(CampaignTickerEvidencePatches.Bindings)
            .Select(binding => AccessTools.Method(AccessTools.Field(typeof(CampaignPeriodicEventManager), binding.Name).FieldType, "PeriodicTickSome"))
            .Distinct();
        Assert.Equal(expected.OrderBy(method => method.DeclaringType!.FullName), targets.OrderBy(method => method.DeclaringType!.FullName));
    }

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
    public void FailedNativeTickerPreservesOriginalExceptionDespiteDiagnosticReadFailure()
    {
        var original = new System.NullReferenceException("native list missing");
        var collector = new CampaignCycleDiagnostics();
        var batch = new CampaignTickerEvidencePatches.TickerBatch { Instance = new object(), Collector = collector,
            Audit = new CampaignTickerAudit { Ticker = "broken", Node = "a" } };
        var result = CampaignTickerEvidencePatches.Finalizer(original, true, batch);
        Assert.Same(original, result);
        var audit = Assert.Single(collector.AuditSnapshot());
        Assert.Equal(1, audit.Mismatches);
        Assert.Contains("native list missing", audit.FirstFailure);
    }

    [Fact]
    public void OnlyNonGenericNativeGroupsAreDetouredAndAllTwentyFieldsAreObserved()
    {
        var targets = CampaignTickerEvidencePatches.GroupTargetMethods().ToArray();
        Assert.Equal(5, targets.Length);
        Assert.All(targets, method => { Assert.False(method.ContainsGenericParameters); Assert.False(method.DeclaringType!.IsGenericType); Assert.Empty(method.GetParameters()); });
        var bindings = targets.SelectMany(method => CampaignTickerEvidencePatches.Bindings(method.Name)).ToArray();
        Assert.Equal(20, bindings.Length); Assert.Equal(20, bindings.Select(value => value.Name).Distinct().Count());
        Assert.All(bindings, value => Assert.NotNull(AccessTools.Field(typeof(CampaignPeriodicEventManager), value.Name)));
        Assert.Equal(new[] { "_customMobilePartyPartialHourlyAiEventTicker", "_patrolPartyPartialHourlyAiEventTicker", "_banditMobilePartyPartialHourlyAiEventTicker",
            "_lordMobilePartyPartialHourlyAiEventTicker", "_partiesWithoutPartyComponentsPartialHourlyAiEventTicker" },
            CampaignTickerEvidencePatches.Bindings("TickPartialHourlyAi").Skip(4).Select(value => value.Name));
    }

    [Fact]
    public void PerTickerAuditPreservesExactCallbackIdentityOrderIncludingOmittedDuplicatedAndReordered()
    {
        foreach (string[] observed in new[] { new[] { "party|a", "native|a" }, new[] { "party|a" },
            new[] { "native|a", "party|a" }, new[] { "party|a", "native|a", "native|a" } })
        {
            var collector = new CampaignCycleDiagnostics();
            var empty = Batch(collector, "empty");
            CampaignTickerEvidencePatches.FinishBatch(null!, empty);
            var batch = Batch(collector, "observed", "party|a", "native|a");
            batch.Completed.AddRange(observed);
            CampaignTickerEvidencePatches.FinishBatch(null!, batch);
            var audits = collector.AuditSnapshot();
            Assert.Equal(0, audits.Single(row => row.Ticker == "empty").Mismatches);
            Assert.Equal(observed.SequenceEqual(new[] { "party|a", "native|a" }), audits.All(row => row.Mismatches == 0));
        }
    }

    [Fact]
    public void GroupAuditStillRejectsChangedNativeCursorDebtAndMembership()
    {
        foreach (int change in new[] { 0, 1, 2 })
        {
            var collector = new CampaignCycleDiagnostics();
            var batch = Batch(collector, "changed", "party|a"); batch.Completed.Add("party|a");
            var ticker = (ObservedTicker)batch.Instance;
            if (change == 0) ticker.Index++;
            if (change == 1) ticker.TickDebt += 0.1;
            if (change == 2) ticker._list.Add(new CharacterObject { StringId = "other" });
            CampaignTickerEvidencePatches.FinishBatch(null!, batch);
            Assert.Equal(1, Assert.Single(collector.AuditSnapshot()).Mismatches);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntentionalObserverSkipIsSeparateFromWriterScheduleFailure(bool writer)
    {
        var collector = new CampaignCycleDiagnostics();
        var batch = Batch(collector, "skipped", "party|a"); batch.Audit.LocalWriter = writer;
        Assert.Null(CampaignTickerEvidencePatches.Finalizer(null!, false, batch));
        Assert.Empty(collector.AuditSnapshot());
        Assert.Equal(1, collector.SimulationSnapshot()["CampaignTicker.skipped.skipped"]);
        Assert.Equal(writer, collector.SimulationSnapshot().ContainsKey("CampaignTicker.writerSkipped.failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippedOuterGroupRetainsEvidenceWithoutTakingASiblingSnapshot(bool writer)
    {
        var collector = new CampaignCycleDiagnostics();
        var batch = Batch(collector, "outer-skipped"); batch.Audit.LocalWriter = writer;
        var finalizer = AccessTools.Method(typeof(CampaignTickerGroupEvidencePatches), "Finalizer");
        Assert.Null(finalizer.Invoke(null, new object?[] { null, false, new[] { batch } }));
        Assert.Empty(collector.AuditSnapshot());
        Assert.Equal(1, collector.SimulationSnapshot()["CampaignTicker.outer-skipped.skipped"]);
        Assert.Equal(writer, collector.SimulationSnapshot().ContainsKey("CampaignTicker.writerSkipped.failed"));
        var original = new InvalidOperationException("outer-before-first-ticker");
        Assert.Same(original, finalizer.Invoke(null, new object?[] { original, true, new[] { batch } }));
        Assert.Equal(1, collector.SimulationSnapshot()["CampaignTicker.outer-skipped.skipped"]);
    }

    private static CampaignTickerEvidencePatches.TickerBatch Batch(CampaignCycleDiagnostics collector, string name, params string[] expected)
    {
        var list = expected.Length == 0 ? new List<MBObjectBase>() : new List<MBObjectBase> { new CharacterObject { StringId = "a" } };
        return new CampaignTickerEvidencePatches.TickerBatch { Collector = collector, Audit = new CampaignTickerAudit { Ticker = name },
            Instance = new ObservedTicker { _list = list, Index = 0, TickDebt = 0.5 }, Ids = list.Select(value => value.StringId).ToArray(),
            Expected = expected, EndIndex = 0, EndDebt = 0.5 };
    }

    private sealed class ObservedTicker
    {
        public List<MBObjectBase> _list = new();
        public int Index { get; set; }
        public double TickDebt { get; set; }
    }

    [Theory]
    [InlineData("CampaignEventDispatcher.DailyTickParty")]
    [InlineData("CampaignEventDispatcher.HourlyTickParty")]
    public void LaterSiblingSpawnCannotInvalidateAnAlreadyCompletedNativeTicker(string callback)
    {
        var collector = new CampaignCycleDiagnostics();
        var party = new CharacterObject { StringId = "a" };
        var ticker = new ObservedTicker { _list = new() { party }, Index = 0, TickDebt = 0.5 };
        var batch = CampaignTickerEvidencePatches.BeginBatch(ticker, callback, collector,
            new CampaignTickerAudit { Ticker = "party" }, 1);
        // Native one-item tick (debt1.5 -> callback a -> debt0.5).
        CampaignTickerEvidencePatches.Completed(callback, party);
        Assert.Null(CampaignTickerEvidencePatches.Finalizer(null!, true, batch));
        // Later DailyTickClan/HourlyTickSettlement can spawn a party in this shared live list.
        ticker._list.Add(new CharacterObject { StringId = "new-lord-or-villager" });
        Assert.Equal(0, Assert.Single(collector.AuditSnapshot()).Mismatches);
        Assert.Equal(1, Assert.Single(collector.AuditSnapshot()).Completed);
    }

    [Fact]
    public void NestedTickerKeepsEachCallbackOnItsOwnBoundaryAndRestoresOuterEvenOnException()
    {
        var collector = new CampaignCycleDiagnostics();
        var outerEntity = new CharacterObject { StringId = "outer" };
        var innerEntity = new CharacterObject { StringId = "inner" };
        var outer = CampaignTickerEvidencePatches.BeginBatch(new ObservedTicker { _list = new() { outerEntity }, TickDebt = 0.5 },
            "outer", collector, new CampaignTickerAudit { Ticker = "outer" }, 1);
        var inner = CampaignTickerEvidencePatches.BeginBatch(new ObservedTicker { _list = new() { innerEntity }, TickDebt = 0.5 },
            "inner", collector, new CampaignTickerAudit { Ticker = "inner" }, 1);
        var error = new InvalidOperationException("native-inner");
        CampaignTickerEvidencePatches.Completed("inner", innerEntity);
        Assert.Same(error, CampaignTickerEvidencePatches.Finalizer(error, true, inner));
        CampaignTickerEvidencePatches.Completed("outer", outerEntity);
        Assert.Null(CampaignTickerEvidencePatches.Finalizer(null!, true, outer));
        Assert.Equal(1, inner.Completed.Count); Assert.Equal("inner|inner", inner.Completed[0]);
        Assert.Equal("outer|outer", Assert.Single(outer.Completed));
        Assert.Equal(1, outer.Depth); Assert.Equal(2, inner.Depth);
        Assert.Equal(0, collector.AuditSnapshot().Single(row => row.Ticker == "outer").Mismatches);
        Assert.Equal(1, collector.AuditSnapshot().Single(row => row.Ticker == "inner").Mismatches);
        // Completed outside a boundary must not leak back into the previous one.
        CampaignTickerEvidencePatches.Completed("outside", outerEntity);
        Assert.Single(outer.Completed);
    }

    [Fact]
    public void FirstFailureContainsAllPredicatesFullSequencesAndExactPhaseBitsWithoutOverwritingFirst()
    {
        var collector = new CampaignCycleDiagnostics();
        var entity = new CharacterObject { StringId = "a" };
        var ticker = new ObservedTicker { _list = new() { entity }, Index = 0, TickDebt = 0.5 };
        var batch = CampaignTickerEvidencePatches.BeginBatch(ticker, "cb", collector,
            new CampaignTickerAudit { Ticker = "native", Node = "A", RoomId = "room", AuthorityId = "world", Epoch = 3 }, 14);
        for (int i = 0; i < 14; i++) CampaignTickerEvidencePatches.Completed("cb", entity);
        ticker.TickDebt = 0.75; // Debt-only defect must remain a mismatch, even with14 matching callbacks.
        CampaignTickerEvidencePatches.Finalizer(null!, true, batch);
        string original = Assert.Single(collector.AuditSnapshot()).FirstFailure;
        var failure = JObject.Parse(original);
        Assert.True((bool)failure["sequenceMatches"]!); Assert.True((bool)failure["membershipMatches"]!);
        Assert.True((bool)failure["indexMatches"]!); Assert.False((bool)failure["debtMatches"]!);
        Assert.Equal(14, ((JArray)failure["expected"]!).Count); Assert.Equal(14, ((JArray)failure["completed"]!).Count);
        Assert.Single((JArray)failure["beforeIds"]!); Assert.Single((JArray)failure["afterIds"]!);
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.75), (long)failure["actualDebtBits"]!);
        Assert.Equal(3, (long)failure["Epoch"]!); Assert.Equal("A", (string)failure["Node"]!);
        Assert.True((int)failure["ThreadId"]! > 0); Assert.NotNull(failure["StartedUtc"]); Assert.NotNull(failure["Frame"]);
        var next = Batch(collector, "native"); next.Audit.Node = "A"; next.Audit.RoomId = "room";
        next.Audit.AuthorityId = "world"; next.Audit.Epoch = 3;
        CampaignTickerEvidencePatches.FinishBatch(new Exception("later"), next);
        Assert.Equal(original, Assert.Single(collector.AuditSnapshot()).FirstFailure);
    }

    [Fact]
    public void InternalMembershipMutationStillFailsAtNativeTickerExit()
    {
        var collector = new CampaignCycleDiagnostics();
        var entity = new CharacterObject { StringId = "a" };
        var ticker = new ObservedTicker { _list = new() { entity }, Index = 0, TickDebt = 0.5 };
        var batch = CampaignTickerEvidencePatches.BeginBatch(ticker, "cb", collector, new CampaignTickerAudit { Ticker = "inside" }, 1);
        CampaignTickerEvidencePatches.Completed("cb", entity);
        ticker._list.Add(new CharacterObject { StringId = "inside-spawn" });
        CampaignTickerEvidencePatches.Finalizer(null!, true, batch);
        var audit = Assert.Single(collector.AuditSnapshot()); Assert.Equal(1, audit.Mismatches);
        Assert.False((bool)JObject.Parse(audit.FirstFailure)["membershipMatches"]!);
    }

    [Fact]
    public void ExcessiveEvidenceIsExplicitFailureAndPredictionCannotLoopWithoutBound()
    {
        Assert.Throws<InvalidOperationException>(() => CampaignTickerEvidencePatches.Expected(new[] { "a" }, 0, 0,
            CampaignTickerEvidencePatches.EvidenceLimit + 2, out _, out _));
        var collector = new CampaignCycleDiagnostics();
        var entity = new CharacterObject { StringId = "a" };
        var batch = CampaignTickerEvidencePatches.BeginBatch(new ObservedTicker { _list = new() { entity } }, "cb",
            collector, new CampaignTickerAudit { Ticker = "bounded" }, 0);
        for (int i = 0; i < CampaignTickerEvidencePatches.EvidenceLimit + 1; i++) CampaignTickerEvidencePatches.Completed("cb", entity);
        CampaignTickerEvidencePatches.Finalizer(null!, true, batch);
        var audit = Assert.Single(collector.AuditSnapshot()); Assert.Equal(1, audit.Mismatches);
        var failure = JObject.Parse(audit.FirstFailure);
        Assert.False((bool)failure["complete"]!); Assert.Equal(CampaignTickerEvidencePatches.EvidenceLimit, ((JArray)failure["completed"]!).Count);
    }

    [Fact]
    public void InstalledClosedNativeMethodsEachObserveOneActualInstanceExactlyOnce()
    {
        var harmony = new Harmony("coop.tests.closed-ticker-dispatch");
        var previous = Campaign.Current;
        ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ContainerProvider.Clear();
            var campaign = (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
            campaign._campaignPeriodicEventManager = new CampaignPeriodicEventManager();
            Campaign.Current = campaign;
            harmony.Patch(AccessTools.PropertyGetter(typeof(TaleWorlds.Engine.Utilities), "EngineFrameNo"),
                prefix: new HarmonyMethod(typeof(CampaignCycleDiagnosticsTests), nameof(FramePrefix)));
            Assert.NotEmpty(harmony.CreateClassProcessor(typeof(CampaignTickerEvidencePatches)).Patch());
            int nativeCalls = 0;
            var bindings = CampaignTickerEvidencePatches.GroupTargetMethods().SelectMany(method => CampaignTickerEvidencePatches.Bindings(method.Name)).ToArray();
            foreach (var binding in bindings)
            {
                var field = AccessTools.Field(typeof(CampaignPeriodicEventManager), binding.Name);
                var ticker = field.GetValue(campaign._campaignPeriodicEventManager);
                AccessTools.Method(typeof(CampaignCycleDiagnosticsTests), nameof(InitializeNativeFixture))
                    .MakeGenericMethod(field.FieldType.GetGenericArguments()[0])
                    .Invoke(null, new object[] { ticker, binding.Name, binding.Callback, (Action)(() => nativeCalls++) });
                AccessTools.Method(field.FieldType, "PeriodicTickSome").Invoke(ticker, new object[] { 1d });
                var observed = CampaignCycleDispatchPatches.For(campaign).AuditSnapshot();
                Assert.Equal(nativeCalls, observed.Length);
                var audit = observed.Single(row => row.Ticker == binding.Name);
                Assert.Equal(1, audit.Batches); Assert.Equal(1, audit.Expected); Assert.Equal(1, audit.Completed);
                Assert.True(audit.Mismatches == 0, audit.FirstFailure);
            }
            Assert.Equal(20, nativeCalls);
            // An unbound same-closed-type ticker must run natively without creating a campaign audit.
            var foreign = new CampaignPeriodicEventManager.PeriodicTicker<Hero>();
            InitializeNativeFixture(foreign, "foreign", "foreign", () => nativeCalls++);
            foreign.PeriodicTickSome(1d);
            Assert.Equal(21, nativeCalls);
            Assert.Equal(20, CampaignCycleDispatchPatches.For(campaign).AuditSnapshot().Length);
            var original = new InvalidOperationException("native action fault retained");
            InitializeNativeFixture(campaign._campaignPeriodicEventManager._dailyTickHeroTicker, "failing-hero",
                "CampaignEventDispatcher.DailyTickHero", () => throw original);
            var thrown = Assert.Throws<TargetInvocationException>(() => AccessTools.Method(
                campaign._campaignPeriodicEventManager._dailyTickHeroTicker.GetType(), "PeriodicTickSome")
                .Invoke(campaign._campaignPeriodicEventManager._dailyTickHeroTicker, new object[] { 1d }));
            Assert.Same(original, thrown.InnerException);
            var failed = CampaignCycleDispatchPatches.For(campaign).AuditSnapshot().Single(row => row.Ticker == "_dailyTickHeroTicker");
            Assert.Equal(2, failed.Batches); Assert.Equal(1, failed.Mismatches);
            var detail = JObject.Parse(failed.FirstFailure);
            Assert.Equal(17, (int)detail["Frame"]!);
            Assert.Contains(original.Message, (string)detail["originalException"]!);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Campaign.Current = previous;
            if (previousContainer != null) ContainerProvider.SetContainer(previousContainer); else ContainerProvider.Clear();
        }
    }

    private static bool FramePrefix(ref int __result) { __result = 17; return false; }

    private static void InitializeNativeFixture<T>(CampaignPeriodicEventManager.PeriodicTicker<T> ticker,
        string id, string callback, Action nativeCall) where T : MBObjectBase
    {
        var entity = (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); entity.StringId = id;
        ticker.Initialize(new MBReadOnlyList<T>(new[] { entity }), value =>
        { nativeCall(); CampaignTickerEvidencePatches.Completed(callback, value); }, false);
        ticker.TickDebt = 0.5;
    }

    [Fact]
    public void ReadOnlyEvidencePatchesBindInstalledNativeOverloads()
    {
        var harmony = new Harmony("coop.tests.native-cycle-evidence");
        try
        {
            foreach (var type in new[] { typeof(CampaignTickerEvidencePatches), typeof(CampaignTickerGroupEvidencePatches), typeof(CampaignTickerCallbackEvidencePatches),
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
