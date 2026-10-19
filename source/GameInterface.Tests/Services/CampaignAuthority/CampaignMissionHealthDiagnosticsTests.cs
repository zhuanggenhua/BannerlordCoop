#if DEBUG
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public sealed class CampaignMissionHealthDiagnosticsTests
{
    private sealed class Sink : ILogEventSink
    {
        internal readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static ILogger Logger(Sink sink) => new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

    private static CampaignMissionHealthDiagnostics.Observation Capture(ILogger logger, int oldHealth, int requested,
        bool mainHero = true, bool paused = true, bool localWriter = false, bool allowed = false) =>
        CampaignMissionHealthDiagnostics.Capture(row =>
        {
            row.HeroId = mainHero ? "Player" : "companion_1";
            row.Reference = "reference:023e7c08";
            row.MainHero = mainHero;
            row.OldHitPoints = oldHealth;
            row.RequestedHitPoints = requested;
            row.Controller = "testclient1";
            row.AuthorityController = paused ? "" : "testclient1";
            row.Epoch = paused ? 6 : 7;
            row.Paused = paused;
            row.LocalWriter = localWriter;
            row.AllowedThread = allowed;
            row.OriginalAllowed = allowed;
            return true;
        }, logger);

    [Fact]
    public void NativeReadOutsideMissionDoesNotInspectHeroOrEmit()
    {
        Assert.Null(Mission.Current);
        var sink = new Sink();
        var row = CampaignMissionHealthDiagnostics.Capture(state => CampaignMissionHealthDiagnostics.Read(null!, 42, state), Logger(sink));
        Assert.Null(row);
        Assert.Empty(sink.Events);
        int postReads = 0;
        var original = new InvalidOperationException("unchanged");
        Assert.Same(original, CampaignMissionHealthDiagnostics.Complete(row!, () => { postReads++; return 9; }, 42, true, original, Logger(sink)));
        Assert.Equal(0, postReads);
    }

    [Theory]
    [InlineData(100, 100, 100, true, true, false, false)]
    [InlineData(100, 43, 43, true, true, false, false)]
    [InlineData(100, 43, 43, false, false, true, false)]
    [InlineData(100, 43, 43, false, false, false, true)]
    public void RecordsActualValuesAndRoleFactsWithoutDecidingPermission(int oldHealth, int requested, int postHealth,
        bool mainHero, bool paused, bool writer, bool allowed)
    {
        var sink = new Sink(); var logger = Logger(sink);
        var row = Capture(logger, oldHealth, requested, mainHero, paused, writer, allowed);
        Assert.Null(CampaignMissionHealthDiagnostics.Complete(row, () => postHealth, requested, true, null!, logger));
        Assert.Equal(oldHealth, row.OldHitPoints); Assert.Equal(requested, row.RequestedHitPoints); Assert.Equal(postHealth, row.PostHitPoints);
        Assert.Equal(mainHero, row.MainHero); Assert.Equal(paused, row.Paused); Assert.Equal(writer, row.LocalWriter);
        Assert.Equal(paused ? 6 : 7, row.Epoch); Assert.Equal(allowed, row.AllowedThread); Assert.Equal(allowed, row.OriginalAllowed);
        Assert.Equal(2, sink.Events.Count); Assert.All(sink.Events, entry => Assert.Equal(LogEventLevel.Debug, entry.Level));
        string after = sink.Events.Last().RenderMessage();
        Assert.Contains("oldHP=" + oldHealth, after); Assert.Contains("requestedHP=" + requested, after); Assert.Contains("postHP=" + postHealth, after);
        Assert.Contains("originalRan=True", after); Assert.Null(row.ObservationError);
    }

    [Fact]
    public void FinalArgumentAndSkippedOriginalAreDistinctFromEntryRequest()
    {
        var sink = new Sink(); var logger = Logger(sink); var row = Capture(logger, 100, 43);
        Assert.Null(CampaignMissionHealthDiagnostics.Complete(row, () => 100, 22, false, null!, logger));
        Assert.Equal(43, row.RequestedHitPoints); Assert.Equal(22, row.FinalArgument); Assert.Equal(100, row.PostHitPoints);
        Assert.Contains("originalRan=False", sink.Events.Last().RenderMessage());
    }

    [Fact]
    public void OriginalExceptionObjectAndExistingStackSurviveFinalization()
    {
        var sink = new Sink(); var logger = Logger(sink); var row = Capture(logger, 100, 43);
        Exception original;
        try { throw new InvalidOperationException("native setter failed"); }
        catch (Exception error) { original = error; }
        string stack = original.StackTrace!;
        Assert.Same(original, CampaignMissionHealthDiagnostics.Complete(row, () => 100, 43, true, original, logger));
        Assert.Equal(stack, original.StackTrace); Assert.Contains("native setter failed", sink.Events.Last().RenderMessage());
    }

    [Fact]
    public void PartialDiagnosticReadFailsExplicitlyWithoutAffectingOriginal()
    {
        var sink = new Sink(); var logger = Logger(sink); long failures = CampaignObjectLifecycleDiagnostics.ObservationFailures;
        var row = CampaignMissionHealthDiagnostics.Capture(state =>
        {
            state.HeroId = "Player"; state.RequestedHitPoints = 43;
            throw new InvalidOperationException("lease diagnostic unavailable");
        }, logger);
        Assert.Equal("Player", row.HeroId); Assert.Contains("lease diagnostic unavailable", row.ObservationError);
        Assert.True(CampaignObjectLifecycleDiagnostics.ObservationFailures > failures);
        Assert.Null(CampaignMissionHealthDiagnostics.Complete(row, () => 43, 43, true, null!, logger));
        Assert.Equal(43, row.PostHitPoints);
    }

    [Fact]
    public void PostReadFailureCannotReplaceTheOriginalException()
    {
        var sink = new Sink(); var logger = Logger(sink); var row = Capture(logger, 100, 43);
        long failures = CampaignObjectLifecycleDiagnostics.ObservationFailures; var original = new Exception("original");
        Assert.Same(original, CampaignMissionHealthDiagnostics.Complete(row, () => throw new Exception("post read unavailable"), 43, true, original, logger));
        Assert.Null(row.PostHitPoints); Assert.Contains("post read unavailable", row.ObservationError);
        Assert.True(CampaignObjectLifecycleDiagnostics.ObservationFailures > failures);
    }

    private sealed class UnformattableException : Exception
    {
        public override string ToString() => throw new FormatException("ToString unavailable");
    }

    [Fact]
    public void ThrowingExceptionFormatterRemainsFailedEvidenceAndReturnsOriginal()
    {
        var sink = new Sink(); var logger = Logger(sink); var error = new UnformattableException();
        long failures = CampaignObjectLifecycleDiagnostics.ObservationFailures;
        var row = CampaignMissionHealthDiagnostics.Capture(_ => throw error, logger);
        Assert.Contains("exception formatting unavailable", row.ObservationError);
        Assert.Same(error, CampaignMissionHealthDiagnostics.Complete(row, () => throw error, 43, true, error, logger));
        Assert.True(CampaignObjectLifecycleDiagnostics.ObservationFailures >= failures + 5);
        Assert.Contains("exception formatting unavailable", sink.Events.Last().RenderMessage());
    }

    [Fact]
    public void LoggerFailureIsCountedAndCannotEscapeOrReplaceOriginal()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(m => m.Write(It.IsAny<LogEventLevel>(), It.IsAny<string>(), It.IsAny<object[]>())).Throws(new Exception("logger unavailable"));
        long failures = CampaignObjectLifecycleDiagnostics.LoggingFailures;
        var row = Capture(logger.Object, 100, 43); var original = new Exception("original");
        Assert.Same(original, CampaignMissionHealthDiagnostics.Complete(row, () => 43, 43, true, original, logger.Object));
        Assert.True(CampaignObjectLifecycleDiagnostics.LoggingFailures >= failures + 2);
    }

    [Fact]
    public void EachInvocationKeepsItsOwnHeroAndLeaseReceipt()
    {
        var sink = new Sink(); var logger = Logger(sink);
        var first = Capture(logger, 100, 43); var second = Capture(logger, 90, 65, false, false, true);
        CampaignMissionHealthDiagnostics.Complete(second, () => 65, 65, true, null!, logger);
        CampaignMissionHealthDiagnostics.Complete(first, () => 43, 43, true, null!, logger);
        Assert.NotSame(first, second); Assert.Equal(6, first.Epoch); Assert.Equal(7, second.Epoch);
        Assert.Equal("Player", first.HeroId); Assert.Equal("companion_1", second.HeroId);
        Assert.Equal(43, first.PostHitPoints); Assert.Equal(65, second.PostHitPoints);
    }

    [Fact]
    public void RealHeroSetterPatchBindsWithoutGenericTargetOrMutationParameters()
    {
        var harmony = new Harmony("coop.tests.mission-health-readonly-probe");
        try
        {
            var patched = harmony.CreateClassProcessor(typeof(CampaignMissionHealthDiagnosticPatch)).Patch();
            Assert.Single(patched);
            Assert.Contains(AccessTools.PropertySetter(typeof(Hero), nameof(Hero.HitPoints)), harmony.GetPatchedMethods());
            Assert.All(harmony.GetPatchedMethods(), method => { Assert.False(method.ContainsGenericParameters); Assert.False(method.DeclaringType!.IsGenericType); });
            var prefix = AccessTools.Method(typeof(CampaignMissionHealthDiagnosticPatch), "Prefix");
            Assert.Equal(typeof(void), prefix.ReturnType);
            Assert.All(prefix.GetParameters().Where(parameter => parameter.Name != "__state"), parameter => Assert.False(parameter.ParameterType.IsByRef));
            var finalizer = AccessTools.Method(typeof(CampaignMissionHealthDiagnosticPatch), "Finalizer");
            Assert.All(finalizer.GetParameters(), parameter => Assert.False(parameter.ParameterType.IsByRef));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
#endif
