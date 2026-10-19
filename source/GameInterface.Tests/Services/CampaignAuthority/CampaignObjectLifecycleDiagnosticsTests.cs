using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using RegistryObjectManager = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority;

public sealed class CampaignObjectLifecycleDiagnosticsTests
{
    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
    private static object Party() => FormatterServices.GetUninitializedObject(typeof(PartyBase));
    private static object ArmyObject() => FormatterServices.GetUninitializedObject(typeof(Army));
    private static ILogger Logger(Sink sink) => new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

    [Fact]
    public void CaptureKeepsExistingIdHandleAndRegistryUntouched()
    {
        var sink = new Sink(); var logger = Logger(sink); var manager = new RegistryObjectManager(logger); var party = Party();
        Assert.True(manager.AddExisting("PartyBase_test", party, 57));
        var handles = manager.GetHandleMap().ToArray();
        var state = CampaignObjectLifecycleDiagnostics.Capture(manager, party, logger);
        Assert.True(state.HadId); Assert.True(state.HadHandle); Assert.Equal("PartyBase_test", state.Id); Assert.Equal(57u, state.Handle);
        Assert.Equal(CampaignObjectLifecycleDiagnostics.Identity(party), state.Identity); Assert.Contains(nameof(CaptureKeepsExistingIdHandleAndRegistryUntouched), state.Stack);
        Assert.Equal(handles, manager.GetHandleMap().ToArray()); Assert.True(manager.TryGetObject(57, out PartyBase same)); Assert.Same(party, same);
        Assert.True(manager.Contains(party)); Assert.All(sink.Events, row => Assert.Equal(LogEventLevel.Debug, row.Level));
    }

    [Fact]
    public void OnlyPartyBaseAndArmyAreObservedAndMissesRemainDebug()
    {
        var sink = new Sink(); var logger = Logger(sink); var manager = new RegistryObjectManager(logger);
        Assert.Null(CampaignObjectLifecycleDiagnostics.Capture(manager, null!, logger));
        Assert.Null(CampaignObjectLifecycleDiagnostics.Capture(manager, new object(), logger));
        foreach (var obj in new[] { Party(), ArmyObject() })
        {
            CampaignObjectLifecycleDiagnostics.LookupMiss(obj, false, true, logger);
            Assert.Contains(CampaignObjectLifecycleDiagnostics.Identity(obj), sink.Events.Last().RenderMessage());
        }
        Assert.Equal(2, sink.Events.Count);
        foreach (var obj in new object?[] { null, new object() }) CampaignObjectLifecycleDiagnostics.LookupMiss(obj!, false, true, logger);
        CampaignObjectLifecycleDiagnostics.LookupMiss(Party(), true, true, logger);
        CampaignObjectLifecycleDiagnostics.LookupMiss(Party(), false, false, logger);
        Assert.Equal(2, sink.Events.Count); Assert.All(sink.Events, row => Assert.Equal(LogEventLevel.Debug, row.Level));
    }

    [Fact]
    public void NullHelpersReportExactCallerWithoutAddingErrorSeverity()
    {
        var sink = new Sink(); var logger = Logger(sink);
        foreach (var method in CampaignNullPartyDiagnosticPatches.TargetMethods())
        {
            CampaignObjectLifecycleDiagnostics.NullParty(null!, method, logger);
            Assert.Contains(method.Name, sink.Events.Last().RenderMessage());
            Assert.Contains(nameof(NullHelpersReportExactCallerWithoutAddingErrorSeverity), sink.Events.Last().RenderMessage());
        }
        Assert.Equal(2, sink.Events.Count); Assert.All(sink.Events, row => Assert.Equal(LogEventLevel.Debug, row.Level));
    }

    [Fact]
    public void FailedDiagnosticReadIsExplicitAndOriginalExceptionIsReturnedUnchanged()
    {
        var sink = new Sink(); var logger = Logger(sink); var manager = new Mock<IObjectManager>(); string id;
        manager.Setup(m => m.TryGetId(It.IsAny<object>(), out id)).Throws(new InvalidOperationException("probe read failed"));
        long failuresBefore = CampaignObjectLifecycleDiagnostics.ObservationFailures;
        var state = CampaignObjectLifecycleDiagnostics.Capture(manager.Object, Party(), logger);
        Assert.True(CampaignObjectLifecycleDiagnostics.ObservationFailures > failuresBefore);
        Assert.Contains("probe read failed", state.ObservationError);
        var original = new NullReferenceException("original Remove failure");
        Assert.Same(original, CampaignObjectLifecycleDiagnostics.Complete(state, true, false, original, logger));
        Assert.Contains("probe read failed", sink.Events.First().RenderMessage());
        Assert.Contains("original Remove failure", sink.Events.Last().RenderMessage());
        manager.Verify(m => m.Remove(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public void ResultAndSkippedStatusAreObservedWithoutCallingRegistry()
    {
        var sink = new Sink(); var logger = Logger(sink); var state = new CampaignObjectLifecycleDiagnostics.Removal { Identity = "reference:000fabdf" };
        Assert.Null(CampaignObjectLifecycleDiagnostics.Complete(state, true, true, null!, logger));
        Assert.Contains("result=True", sink.Events.Last().RenderMessage());
        Assert.Null(CampaignObjectLifecycleDiagnostics.Complete(state, false, false, null!, logger));
        Assert.Contains("ranOriginal=False", sink.Events.Last().RenderMessage());
        var error = new Exception("untouched");
        Assert.Same(error, CampaignObjectLifecycleDiagnostics.Complete(null!, true, false, error, logger));
        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void DiagnosticLoggerFailureCannotReplaceOriginalException()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(m => m.Write(It.IsAny<LogEventLevel>(), It.IsAny<string>(), It.IsAny<object[]>())).Throws(new Exception("sink unavailable"));
        long failuresBefore = CampaignObjectLifecycleDiagnostics.LoggingFailures;
        var original = new InvalidOperationException("original");
        Assert.Same(original, CampaignObjectLifecycleDiagnostics.Complete(new CampaignObjectLifecycleDiagnostics.Removal(), true, false, original, logger.Object));
        Assert.True(CampaignObjectLifecycleDiagnostics.LoggingFailures > failuresBefore);
    }

    [Fact]
    public void FailureCountersAreVisibleInExistingCycleAcceptanceKeys()
    {
        var collector = new CampaignCycleDiagnostics();
        CampaignObjectLifecycleDiagnostics.RecordFailure("observation", collector);
        CampaignObjectLifecycleDiagnostics.RecordFailure("logging", collector);
        Assert.Equal(1, collector.SimulationSnapshot()["CampaignLifecycleProbe.observation.failed"]);
        Assert.Equal(1, collector.SimulationSnapshot()["CampaignLifecycleProbe.logging.failed"]);
    }

    [Fact]
    public void NongenericPatchesBindAndRealRemoveAndOriginalErrorsRemainUnchanged()
    {
        var harmony = new Harmony("coop.tests.lifecycle-readonly-probes");
        try
        {
            int targets = 0;
            foreach (var type in new[] { typeof(CampaignObjectRemovalDiagnosticPatches), typeof(CampaignObjectLookupDiagnosticPatches), typeof(CampaignNullPartyDiagnosticPatches) })
            {
                var methods = harmony.CreateClassProcessor(type).Patch(); targets += methods.Count;

            }
            Assert.Equal(4, targets);
            Assert.All(harmony.GetPatchedMethods(), method => { Assert.False(method.ContainsGenericParameters); Assert.False(method.DeclaringType!.IsGenericType); });
            var sink = new Sink(); var logger = Logger(sink); var manager = new RegistryObjectManager(logger);
            foreach (var obj in new[] { Party(), ArmyObject() })
            {
                Assert.True(manager.AddExisting("test_" + obj.GetType().Name, obj, obj is Army ? 58u : 57u));
                Assert.True(manager.TryGetId(obj, out var id)); Assert.True(manager.TryGetHandle(obj, out var handle));
                Assert.True(manager.Remove(obj)); Assert.False(manager.Contains(obj)); Assert.False(manager.Remove(obj));
                Assert.False(manager.TryGetId(obj, out _)); Assert.False(manager.TryGetHandle(obj, out _));
                Assert.False(manager.TryGetIdWithLogging(obj, out _));
            }
            Assert.Equal(2, sink.Events.Count(row => row.Level == LogEventLevel.Error));
            Assert.All(sink.Events.Where(row => row.Level == LogEventLevel.Error), row => Assert.Contains("Failed to get id", row.RenderMessage()));
            Assert.False(MobilePartyExtensions.IsPlayerParty(null!)); Assert.False(MobilePartyExtensions.IsControlledByThisInstance(null!));
            Assert.False(manager.Remove(null!));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
