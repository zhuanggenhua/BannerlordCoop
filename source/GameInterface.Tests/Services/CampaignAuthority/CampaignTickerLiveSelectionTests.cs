using GameInterface.Services.CampaignAuthority;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignTickerLiveSelectionTests
{
    // These selection tests run the actual native PeriodicTickSome body with a
    // managed callback-boundary seam. Independent CLR4 fixtures run the actual
    // production Dispatcher Prefix/Finalizer, without this seam.
    private sealed class Run
    {
        public readonly CampaignPeriodicEventManager.PeriodicTicker<Hero> Ticker = new();
        public IList<Hero> List = null!;
        public readonly List<string> Calls = new();
        public CampaignCycleDiagnostics Collector = new();
        public CampaignTickerEvidencePatches.TickerBatch Batch = null!;
        public Exception? Error;
        public CampaignTickerAudit Audit => Assert.Single(Collector.AuditSnapshot());
    }

    private static Hero Hero(string id)
    {
        var hero = (Hero)RuntimeHelpers.GetUninitializedObject(typeof(Hero));hero.StringId = id;return hero;
    }

    private static Run Execute(string[] ids, double elapsed, Action<Run, Hero>? work = null,
        Action<Run>? before = null, Action<Run>? after = null, Func<Run, Hero, Hero>? forward = null,
        int deliveries = 1, bool runNative = true)
    {
        var run = new Run();
        var list = new MBReadOnlyList<Hero>(ids.Select(Hero).ToArray());run.List = list;
        run.Ticker.Initialize(list, entity =>
        {
            run.Calls.Add(entity.StringId);
            for (int i = 0; i < deliveries; i++)
            {
                var boundary = CampaignTickerEvidencePatches.BeginCallback("cb", forward?.Invoke(run, entity) ?? entity);
                Exception? error = null;
                try { work?.Invoke(run, entity); }
                catch (Exception original) { error = original;throw; }
                finally { Assert.Same(error, CampaignTickerEvidencePatches.EndCallback(error!, true, boundary)); }
            }
        }, false);
        run.Ticker.TickDebt = 0.5;
        run.Batch = CampaignTickerEvidencePatches.BeginBatch(run.Ticker, "cb", run.Collector,
            new CampaignTickerAudit { Ticker = "live", Node = "a", RoomId = "room", AuthorityId = "world", Epoch = 3,
                LeaseKnown = true, LocalWriter = true }, elapsed);
        CampaignTickerEvidencePatches.EnableLiveSelection(run.Batch);
        try { before?.Invoke(run);if (runNative) run.Ticker.PeriodicTickSome(elapsed);after?.Invoke(run); }
        catch (Exception error) { run.Error = error; }
        finally { Assert.Same(run.Error, CampaignTickerEvidencePatches.CompleteBatch(run.Error!, true, run.Batch)); }
        return run;
    }

    [Theory]
    [InlineData("current")]
    [InlineData("earlier")]
    [InlineData("append")]
    public void ObservedNativeInternalMutationUsesPreviousPostReferencesAndInitialCountDebt(string change)
    {
        var run = Execute(new[] { "h0", "h1", "h2", "h3" }, 0.75, (r, entity) =>
        {
            if (change == "current" && r.Calls.Count == 1) r.List.Remove(entity);
            if (change == "earlier" && r.Calls.Count == 2) r.List.RemoveAt(0);
            if (change == "append" && r.Calls.Count == 1) r.List.Add(Hero("new"));
        });
        Assert.Null(run.Error);Assert.Equal(0, run.Audit.Mismatches);
        Assert.Equal(change == "current" ? new[] { "h0", "h2", "h3" } :
            change == "earlier" ? new[] { "h0", "h1", "h3" } : new[] { "h0", "h1", "h2" }, run.Calls);
        Assert.Equal(0.5, run.Ticker.TickDebt);Assert.Equal(3, run.Audit.Expected);Assert.Equal(3, run.Audit.Completed);
        Assert.Equal(1, run.Audit.MembershipChanges);
        var evidence = JObject.Parse(run.Audit.FirstMembershipChange);
        Assert.Equal(4, ((JArray)evidence["beforeIds"]!).Count);
        Assert.Equal(change == "append" ? 5 : 3, ((JArray)evidence["afterIds"]!).Count);
        Assert.Equal(3, (int)evidence["Epoch"]!);Assert.NotNull(evidence["StartedUtc"]);
    }

    [Fact]
    public void NativeWrapAndMultipleRoundsDoNotDeduplicateEntitiesOrRecomputeDebtAfterAppend()
    {
        var wrapped = Execute(new[] { "a", "b", "c", "d" }, 1, (r, entity) =>
        { if (r.Calls.Count == 1) r.List.Remove(entity); });
        Assert.Equal(new[] { "a", "c", "d", "b" }, wrapped.Calls);Assert.Equal(0, wrapped.Audit.Mismatches);
        var rounds = Execute(new[] { "a", "b", "c" }, 2);
        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c" }, rounds.Calls);Assert.Equal(0, rounds.Audit.Mismatches);
        Assert.Equal(0.5, rounds.Ticker.TickDebt);
    }

    [Fact]
    public void SameIdsDoNotMakeAnUnpredictedObjectReferenceItsOwnExpectation()
    {
        var run = Execute(new[] { "a", "b" }, 1, forward: (_, entity) => Hero(entity.StringId));
        Assert.Null(run.Error);Assert.Equal(1, run.Audit.Mismatches);
        Assert.False(run.Batch.LiveSelectionMatches);
        Assert.Equal(run.Audit.Expected, run.Audit.Completed);
    }

    [Fact]
    public void MissingDuplicatedWrongIdentityAndReorderedDeliveriesRemainFailures()
    {
        Assert.Equal(1, Execute(new[] { "a", "b" }, 1, deliveries: 0).Audit.Mismatches);
        Assert.Equal(1, Execute(new[] { "a", "b" }, 1, deliveries: 2).Audit.Mismatches);
        Assert.Equal(1, Execute(new[] { "a", "b" }, 1, forward: (r, entity) => r.List.First(x => !ReferenceEquals(x, entity))).Audit.Mismatches);
    }

    [Fact]
    public void EarlyReturnWithOutstandingDueDebtCannotPassAnEmptyObservedSequence()
    {
        var run = Execute(new[] { "a" }, 1, before: r => r.Ticker.TickDebt = 1.5, runNative: false);
        Assert.Empty(run.Calls);Assert.Equal(1, run.Audit.Mismatches);
        Assert.Contains("independent initial native budget", run.Batch.DiagnosticError);
        Assert.Equal(1, run.Batch.InitialExpectedCount);Assert.Empty(run.Batch.LiveExpected);
        Assert.Equal(0.5, run.Batch.InitialEndDebt);Assert.Equal(1.5, run.Batch.LiveDebt);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("same-id-reference")]
    [InlineData("index")]
    [InlineData("debt")]
    public void UnobservedMutationAndWrongCursorOrDebtAreNeverAccepted(string change)
    {
        var run = Execute(new[] { "a", "b", "c" }, 1,
            before: r => { if (change == "before") r.List.RemoveAt(0); },
            after: r =>
            {
                if (change == "after") r.List.Add(Hero("outside"));
                if (change == "same-id-reference") r.List[0] = Hero(r.List[0].StringId);
                if (change == "index") r.Ticker.Index++;
                if (change == "debt") r.Ticker.TickDebt += 0.01;
            });
        Assert.Equal(1, run.Audit.Mismatches);
    }

    [Fact]
    public void PassingSameIdReferenceChangeIsVisibleAndOnlyFirstCompleteTransitionIsRetained()
    {
        var run = Execute(new[] { "a", "b", "c" }, 1, (r, _) =>
        { if (r.Calls.Count < 3) r.List[2] = Hero("c"); });
        Assert.Equal(0, run.Audit.Mismatches);Assert.Equal(2, run.Audit.MembershipChanges);
        var first = JObject.Parse(run.Audit.FirstMembershipChange);
        Assert.True((bool)first["referenceSequenceChanged"]!);
        Assert.Equal((JArray)first["beforeIds"]!, (JArray)first["afterIds"]!, JToken.EqualityComparer);
        Assert.NotEqual(first["beforeReferenceFingerprints"]!.ToString(), first["afterReferenceFingerprints"]!.ToString());
        Assert.Equal("a", (string)first["EntityId"]!);
    }

    [Fact]
    public void EmptyNativeListIsNoWorkButDeletionToEmptyWithRemainingDebtPreservesNativeException()
    {
        var empty = Execute(Array.Empty<string>(), 2);Assert.Empty(empty.Calls);
        Assert.Equal(0, empty.Audit.Mismatches);Assert.Equal(0, empty.Ticker.TickDebt);
        var lastRemoved = Execute(new[] { "last" }, 1, (r, _) => r.List.Clear());
        Assert.Null(lastRemoved.Error);Assert.Empty(lastRemoved.List);
        Assert.Equal(0, lastRemoved.Audit.Mismatches);Assert.Equal(0.5, lastRemoved.Ticker.TickDebt);
        var removed = Execute(new[] { "a", "b" }, 1, (r, _) => r.List.Clear());
        Assert.IsType<ArgumentOutOfRangeException>(removed.Error);Assert.Equal(1, removed.Audit.Mismatches);
        Assert.Contains(removed.Error!.Message, removed.Audit.FirstFailure);
    }

    [Fact]
    public void NestedNativeTickerRestoresOuterSelectionButUnscopedNestedCallbackDoesNotAdvanceIt()
    {
        Run? inner = null;
        var outer = Execute(new[] { "a", "b" }, 1, (r, _) =>
        { if (r.Calls.Count == 1) inner = Execute(new[] { "inner" }, 2); });
        Assert.Equal(0, outer.Audit.Mismatches);Assert.Equal(0, inner!.Audit.Mismatches);
        var unscoped = Execute(new[] { "a" }, 1, (r, entity) =>
        {
            var callback = CampaignTickerEvidencePatches.BeginCallback("cb", entity);
            CampaignTickerEvidencePatches.EndCallback(null!, true, callback);
        });
        Assert.Equal(1, unscoped.Audit.Mismatches);Assert.Single(unscoped.Batch.Completed);
    }

    [Fact]
    public void OriginalCallbackExceptionKeepsItsReferenceAndNativePreDecrementDebt()
    {
        var original = new InvalidOperationException("original-native-callback-reference");
        var run = Execute(new[] { "a", "b" }, 1, (_, _) => throw original);
        Assert.Same(original, run.Error);Assert.Equal(2.5, run.Ticker.TickDebt);
        Assert.Equal(1, run.Audit.Mismatches);Assert.Null(run.Batch.DiagnosticError);
        Assert.Empty(run.Batch.Completed);Assert.Contains(original.Message, run.Audit.FirstFailure);
    }

    [Fact]
    public void LiveEvidenceCapIsExplicitIncompleteAndDoesNotAccumulateWholeListHistory()
    {
        var run = Execute(new[] { "a" }, CampaignTickerEvidencePatches.EvidenceLimit + 2);
        Assert.Equal(1, run.Audit.Mismatches);Assert.NotNull(run.Batch.DiagnosticError);
        Assert.Equal(CampaignTickerEvidencePatches.EvidenceLimit, run.Batch.LiveExpected.Count);
        Assert.Equal(CampaignTickerEvidencePatches.EvidenceLimit, run.Batch.Completed.Count);
        Assert.Single(run.Batch.ObservedEntities);Assert.Null(run.Audit.FirstMembershipChange);
    }
}
