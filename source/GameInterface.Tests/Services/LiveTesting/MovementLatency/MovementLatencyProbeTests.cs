#if DEBUG
using Autofac;
using Common;
using Common.Commands;
using Common.Util;
using GameInterface.Services.LiveTesting.MovementLatency;
using GameInterface.Services.MobileParties.Data;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.LiveTesting.MovementLatency;

[Collection(nameof(ModInformationRoleCollection))]
public sealed class MovementLatencyProbeTests
{
    [Fact]
    public void UnarmedObservationsDoNotSampleOrInstallHooks()
    {
        var env = new Environment(); var hooks = new Hooks(); using var probe = new MovementLatencyProbe(env, hooks);
        probe.Frame(); probe.Observe(env.Actors[0].Party, "native_position");
        Assert.False(probe.Tracks(env.Actors[0].Party)); Assert.Equal(0, env.SampleCount); Assert.Equal(0, hooks.Starts);
        Assert.Empty(probe.Read(0, 1).Rows);
    }

    [Fact]
    public void BindingFailureRetainsPreviousEvidenceAndDoesNotInstallHooks()
    {
        var env = new Environment(); var hooks = new Hooks(); using var probe = new MovementLatencyProbe(env, hooks);
        probe.Begin("first"); probe.Stop(); env.Bind = false;
        Assert.Throws<InvalidOperationException>(() => probe.Begin("failed"));
        Assert.Equal("first", probe.Read(0, 256).Label); Assert.Equal(2, probe.Read(0, 256).Rows.Length); Assert.Equal(1, hooks.Starts);
    }

    [Fact]
    public void FailedHookStartPreservesExceptionDisarmsAndCannotReportEmptyCaptureComplete()
    {
        var env = new Environment(); var failure = new InvalidOperationException("hook start failed");
        var hooks = new Hooks { StartFailure = failure };
        using var probe = new MovementLatencyProbe(env, hooks);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => probe.Begin("arm-failed")));
        var page = probe.Read(0, 256);
        Assert.False(page.Complete); Assert.False(page.Armed); Assert.Empty(page.Rows);
        Assert.Contains("arm_failed", page.Fault); Assert.Contains("hook start failed", page.Fault);
        Assert.Equal("arm_failed", page.StopReason); Assert.Equal(1, hooks.Starts); Assert.Equal(1, hooks.Stops);
        probe.Frame(); probe.Observe(env.Actors[0].Party, "native_position");
        Assert.Equal(0, env.SampleCount); Assert.False(probe.Stop().Complete); Assert.Equal(1, hooks.Stops);
    }

    [Fact]
    public void CapacityDoesNotOverwriteOriginalRowsAndOverflowCannotComplete()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks(), 3);
        probe.Begin("cap"); probe.Frame(); probe.Frame(); var page = probe.Stop();
        Assert.True(page.Overflow); Assert.Equal(3, page.DroppedRows); Assert.False(page.Complete);
        Assert.Equal(new long[] { 0, 1, 2 }, page.Rows.Select(r => r.Seq)); Assert.Equal("begin", page.Rows[0].Kind);
    }

    [Fact]
    public void PaginationIsInclusiveStableAndHasNoDuplicateOrLostRows()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks());
        probe.Begin("pages"); for (int i = 0; i < 11; i++) probe.Frame(); probe.Stop();
        var seq = new List<long>(); long cursor = 0; MovementLatencyPage page;
        do { page = probe.Read(cursor, 3); seq.AddRange(page.Rows.Select(r => r.Seq)); cursor = page.NextFromSeq; } while (page.HasMore);
        Assert.Equal(Enumerable.Range(0, 24).Select(i => (long)i), seq);
        Assert.Empty(probe.Read(cursor, 256).Rows); Assert.Equal(cursor, probe.Read(cursor, 256).NextFromSeq);
        Assert.Equal(probe.Read(0, 3).Rows.Select(r => r.Clock.StopwatchTicks), probe.Read(0, 3).Rows.Select(r => r.Clock.StopwatchTicks));
    }

    [Fact]
    public void StoppedReadsRefreshCalibrationClockButNeverRestampHistoricalRows()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks());
        probe.Begin("clock"); probe.Stop(); var first = probe.Read(0, 1); var second = probe.Read(0, 1);
        Assert.True(second.Clock.StopwatchTicks > first.Clock.StopwatchTicks);
        Assert.Equal(first.Rows[0].Clock.StopwatchTicks, second.Rows[0].Clock.StopwatchTicks);
        Assert.True(first.Clock.StopwatchTicks > first.Rows[0].Clock.StopwatchTicks);
        Assert.Contains("NOT_OBSERVED", first.ClockCalibration); Assert.Contains("NOT_OBSERVED", first.Association);
    }

    [Fact]
    public void StopUninstallsOnceAndLateObserversCannotAppend()
    {
        var env = new Environment(); var hooks = new Hooks(); using var probe = new MovementLatencyProbe(env, hooks);
        probe.Begin("stop"); var stopped = probe.Stop(); probe.Frame(); probe.Observe(env.Actors[0].Party, "visual_frame", 1, 2); probe.Stop();
        Assert.True(stopped.Complete); Assert.False(stopped.Armed); Assert.Equal(1, hooks.Stops);
        Assert.Equal(stopped.NextSeq, probe.Read(0, 256).NextSeq);
    }

    [Fact]
    public void NewLabelGetsIndependentBufferButCannotReuseOldLabelOrReplaceAnArmedRun()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks());
        probe.Begin("unique"); Assert.Throws<InvalidOperationException>(() => probe.Begin("other")); probe.Stop();
        Assert.Throws<InvalidOperationException>(() => probe.Begin("unique"));
        var next = probe.Begin("second"); Assert.Equal(2, next.NextSeq); Assert.Equal(0, next.Rows[0].Seq);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unsafe/path")]
    [InlineData("has space")]
    public void InvalidLabelsCannotArm(string label)
    {
        var hooks = new Hooks(); using var probe = new MovementLatencyProbe(new Environment(), hooks);
        Assert.Throws<ArgumentException>(() => probe.Begin(label)); Assert.Equal(0, hooks.Starts);
    }

    [Fact]
    public void BadCursorsAndPageSizesAreRejectedWithoutAdvancingEvidence()
    {
        using var probe = new MovementLatencyProbe(new Environment(), new Hooks()); probe.Begin("cursor");
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Read(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Read(3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Read(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Read(0, 257));
        Assert.Equal(2, probe.Read(0, 256).NextSeq);
    }

    [Fact]
    public void CampaignChangeUninstallsAndDropsOldActorReferencesButRetainsTraceAsInvalid()
    {
        var env = new Environment(); var hooks = new Hooks(); using var probe = new MovementLatencyProbe(env, hooks);
        probe.Begin("campaign"); env.Token = new object(); probe.Frame(); var page = probe.Read(0, 256);
        Assert.False(page.Armed); Assert.False(page.Complete); Assert.Equal("campaign_changed", page.Fault);
        Assert.Equal(1, hooks.Stops); Assert.False(probe.Tracks(env.Actors[0].Party)); Assert.Equal(2, page.NextSeq);
    }

    [Fact]
    public void EpochChangeIsVisibleAndCannotBecomeComplete()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks());
        probe.Begin("epoch"); env.Epoch++; probe.Frame(); var page = probe.Stop();
        Assert.Equal("epoch_changed", page.Fault); Assert.False(page.Complete);
        Assert.Equal(new long[] { 5, 5, 6, 6 }, page.Rows.Select(r => r.Epoch));
    }

    [Fact]
    public void InputApplyModelAndVisualKeepIndependentObservationKindsAndTargets()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks()); probe.Begin("stages");
        var party = env.Actors[0].Party; var input = probe.InputEnter(party, 30, 40); env.Target = 30; probe.InputExit(party, input);
        long apply = probe.ApplyEnter(party, "remote", 80, 90); probe.ApplyExit(party, apply, true);
        probe.Observe(party, "native_position"); probe.Observe(party, "visual_frame", 3, 4);
        var rows = probe.Stop().Rows;
        Assert.Equal(new[] { "begin", "begin", "input_enter", "input_exit", "apply_enter", "apply_exit", "native_position", "visual_frame" }, rows.Select(r => r.Kind));
        Assert.Equal(30, rows[2].RequestedX); Assert.Equal(30, rows[3].TargetX);
        Assert.Equal(80, rows[4].RequestedX); Assert.Equal("remote", rows[4].OriginController);
        Assert.NotEqual(input, apply); Assert.Equal(apply, rows[5].ObservationId);
        Assert.Null(rows[6].VisualX); Assert.Equal(3, rows[7].VisualX);
    }

    [Fact]
    public void RejectedApplyOrDiagnosticExceptionCannotBeReportedComplete()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks()); probe.Begin("reject");
        var party = env.Actors[0].Party; long id = probe.ApplyEnter(party, "remote", 4, 5); probe.ApplyExit(party, id, false);
        var page = probe.Stop(); Assert.Equal("snapshot_apply_rejected", page.Fault); Assert.False(page.Complete);
        probe.Begin("error"); probe.Fail(new InvalidOperationException("retained evidence"));
        Assert.Contains("retained evidence", probe.Stop().Fault);
    }

    [Fact]
    public void FrozenObservationNeverChangesWorldObjectsOrClearsFreeze()
    {
        var env = new Environment { Frozen = true }; var party = env.Actors[0].Party;
        party.PartyMoveMode = MoveModeType.Party; party.MoveTargetParty = env.Actors[1].Party;
        using var probe = new MovementLatencyProbe(env, new Hooks()); probe.Begin("frozen");
        probe.InputEnter(party, 10, 20); probe.Frame(); probe.Observe(party, "native_position"); probe.Stop();
        Assert.True(env.Frozen); Assert.Equal(MoveModeType.Party, party.PartyMoveMode); Assert.Same(env.Actors[1].Party, party.MoveTargetParty);
        Assert.All(probe.Read(0, 256).Rows, r => Assert.True(r.Frozen));
    }

    [Fact]
    public void UnregisteredNpcIsNotSampledAndParallelWritersHaveOrderedUniqueSequences()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks()); probe.Begin("parallel");
        probe.Observe(ObjectHelper.SkipConstructor<MobileParty>(), "native_position");
        Parallel.For(0, 200, i => probe.Observe(env.Actors[i % 2].Party, "native_position"));
        var rows = probe.Stop().Rows;
        Assert.Equal(202, rows.Length); Assert.Equal(Enumerable.Range(0, 202).Select(i => (long)i), rows.Select(r => r.Seq));
        Assert.True(rows.Zip(rows.Skip(1), (a, b) => a.Clock.StopwatchTicks < b.Clock.StopwatchTicks).All(x => x));
    }

    [Fact]
    public void DisposeDisarmsAndCannotRearmOrReadDisposedScope()
    {
        var hooks = new Hooks(); var probe = new MovementLatencyProbe(new Environment(), hooks); probe.Begin("dispose");
        probe.Dispose(); probe.Dispose(); Assert.Equal(1, hooks.Stops);
        Assert.Throws<ObjectDisposedException>(() => probe.Begin("next")); Assert.Throws<ObjectDisposedException>(() => probe.Read(0, 1));
    }

    [Fact]
    public void InstalledDllTargetsHaveActualExpectedSignaturesAndReadOnlyObserverPatches()
    {
        var targets = NativeMovementLatencyHooks.ResolveTargets(); Assert.Equal(6, targets.Count);
        Assert.Equal("HandleLeftMouseButtonClick", targets["input"].Name); Assert.Equal(typeof(void), ((System.Reflection.MethodInfo)targets["input"]).ReturnType);
        Assert.Equal(typeof(MobileParty), targets["parallelPosition"].DeclaringType);
        Assert.True(targets["parallelPosition"].GetParameters()[0].ParameterType.IsByRef);
        Assert.Equal(typeof(bool), ((System.Reflection.MethodInfo)targets["apply"]).ReturnType);
        Assert.True(targets["apply"].GetParameters()[2].IsOut);
        Assert.Equal("Tick", targets["visual"].Name); Assert.Equal(4, targets["visual"].GetParameters().Length);
        var original = new InvalidOperationException("original"); Assert.Same(original, NativeMovementLatencyHooks.OriginalException(original));
    }

    [Fact]
    public void RealHookInstallAndStopAreScopedAndLeaveProductionPatchesAlone()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new NativeMovementLatencyHooks());
        var targets = NativeMovementLatencyHooks.ResolveTargets();
        var before = targets.ToDictionary(p => p.Key, p => Harmony.GetPatchInfo(p.Value)?.Owners.ToArray() ?? Array.Empty<string>());
        probe.Begin("native-hooks");
        foreach (var method in targets.Values) Assert.Contains(Harmony.GetPatchInfo(method).Owners, id => id.StartsWith("Coop.Debug.MovementLatency.", StringComparison.Ordinal));
        probe.Stop();
        foreach (var target in targets) Assert.Equal(before[target.Key].OrderBy(x => x), (Harmony.GetPatchInfo(target.Value)?.Owners.ToArray() ?? Array.Empty<string>()).OrderBy(x => x));
    }

    [Fact]
    public void ModuleResolvesOneProbePerScopeWithoutInstallingOrAutoActivatingHooks()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<MovementLatencyModule>();
        builder.RegisterInstance(Moq.Mock.Of<global::GameInterface.Services.Players.IPlayerManager>());
        builder.RegisterInstance(Moq.Mock.Of<global::GameInterface.Services.ObjectManager.IObjectManager>());
        builder.RegisterInstance(Moq.Mock.Of<ISessionIdentity>());
        builder.RegisterInstance(Moq.Mock.Of<global::GameInterface.Services.Entity.IControllerIdProvider>());
        using var container = builder.Build();
        using var first = container.BeginLifetimeScope(); using var second = container.BeginLifetimeScope();
        Assert.Same(first.Resolve<IMovementLatencyProbe>(), first.Resolve<IMovementLatencyProbe>());
        Assert.NotSame(first.Resolve<IMovementLatencyProbe>(), second.Resolve<IMovementLatencyProbe>());
        foreach (var method in NativeMovementLatencyHooks.ResolveTargets().Values)
            Assert.DoesNotContain(Harmony.GetPatchInfo(method)?.Owners ?? Enumerable.Empty<string>(),
                id => id.StartsWith("Coop.Debug.MovementLatency.", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsExposeCamelCaseFreshClockAndFailedOverflowWithoutChangingInput()
    {
        var env = new Environment(); using var probe = new MovementLatencyProbe(env, new Hooks(), 3);
        var args = new CoopCommandArgsFactory();
        var begin = new MovementLatencyCommands.BeginCommand(probe).ProcessCommand(args.FromValues(new[] { "commands" }));
        Assert.True(begin.Succeeded); var json = JObject.Parse(begin.Output); Assert.Equal(8192, MovementLatencyProbe.DefaultCapacity);
        Assert.NotNull(json["clock"]?["stopwatchTicks"]); Assert.NotNull(json["rows"]?[0]?["actorController"]);
        probe.Frame();
        var stopped = new MovementLatencyCommands.StopCommand(probe).ProcessCommand(args.FromValues(Array.Empty<string>()));
        Assert.False(stopped.Succeeded); Assert.Equal("probe_invalid", stopped.ErrorCode);
        var read = new MovementLatencyCommands.ReadCommand(probe).ProcessCommand(args.FromValues(new[] { "0", "2" }));
        Assert.False(read.Succeeded); Assert.True((bool)JObject.Parse(read.Output)["overflow"]!);
    }

    private sealed class Hooks : IMovementLatencyHooks
    {
        public int Starts, Stops;
        public Exception? StartFailure;
        public void Start(MovementLatencyProbe probe)
        { Starts++; if (StartFailure != null) throw StartFailure; }
        public void Stop() => Stops++;
    }
    private sealed class Environment : IMovementLatencyEnvironment
    {
        public object Token = new(); public bool Bind = true, Frozen; public long Epoch = 5; public float Target = 1;
        public int SampleCount; private long ticks;
        public readonly MovementLatencyActor[] Actors =
        {
            new() { Party = ObjectHelper.SkipConstructor<MobileParty>(), Controller = "a", Id = "party-a", Handle = 1 },
            new() { Party = ObjectHelper.SkipConstructor<MobileParty>(), Controller = "b", Id = "party-b", Handle = 2 },
        };
        public object CampaignToken => Token;
        public bool TryBind(out string campaign, out MovementLatencyActor[] actors, out string error)
        { campaign = "campaign"; actors = Actors; error = "binding refused"; return Bind; }
        public MovementLatencyClock ReadClock() => new() { Pid = 123, Frequency = 1000, StopwatchTicks = ++ticks, Utc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks), EngineFrame = 1 };
        public MovementLatencyRow Sample(MovementLatencyActor actor, string kind, long observationId)
        {
            SampleCount++;
            return new() { Kind = kind, ObservationId = observationId, Clock = ReadClock(), Campaign = "campaign", Epoch = Epoch,
                ActorController = actor.Controller, LocalController = "a", PartyId = actor.Id, Handle = actor.Handle,
                TargetX = Target, TargetY = Target, Frozen = Frozen };
        }
    }
}
#endif
