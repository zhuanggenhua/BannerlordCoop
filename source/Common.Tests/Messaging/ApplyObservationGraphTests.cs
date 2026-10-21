using Common.Messaging;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class ApplyObservationGraphTests
{
    [Fact]
    public void SameSourceActivationValidatesEachObserverOncePerQuery()
    {
        int validations = 0, completed = 0;
        using (MessageApplyScope.Observe(() => { validations++; return true; }, _ => { }, () => completed++))
        {
            WithAliasedActivations(8, () =>
            {
                validations = 0;
                Assert.True(MessageApplyScope.IsCurrent);
                Assert.Equal(1, validations);
                Assert.Equal(0, completed);
            });
            Assert.Equal(0, completed);
        }
        Assert.Equal(1, completed);
    }

    [Fact]
    public void SameSourceActivationNotifiesEachObserverOncePerFailurePropagation()
    {
        int failures = 0, completed = 0;
        using (MessageApplyScope.Observe(() => true, _ => failures++, () => completed++))
            WithAliasedActivations(8, () =>
            {
                MessageApplyScope.Fail("first");
                Assert.Equal(1, failures);
                MessageApplyScope.Fail("second");
                Assert.Equal(2, failures);
            });
        Assert.Equal(1, completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctBranchesVisitTheirSharedAncestorOnlyOnce(bool fail)
    {
        int root = 0, left = 0, right = 0;
        int rootCompleted = 0, leftCompleted = 0, rightCompleted = 0;
        using (MessageApplyScope.Observe(() => { if (!fail) root++; return true; }, _ => root++, () => rootCompleted++))
        {
            MessageApplyScope.RetainedObservation captured;
            using (MessageApplyScope.Observe(() => { if (!fail) left++; return true; }, _ => left++, () => leftCompleted++))
                captured = MessageApplyScope.RetainCurrent();
            using (MessageApplyScope.Observe(() => { if (!fail) right++; return true; }, _ => right++, () => rightCompleted++))
            using (captured.Activate())
            {
                root = left = right = 0;
                if (fail) MessageApplyScope.Fail("shared source failure");
                else Assert.True(MessageApplyScope.IsCurrent);
                Assert.Equal(1, root);
                Assert.Equal(1, left);
                Assert.Equal(1, right);
                Assert.Equal(0, rootCompleted);
                Assert.Equal(0, leftCompleted);
                Assert.Equal(0, rightCompleted);
            }
            captured.Complete();
            Assert.Equal(1, leftCompleted);
            Assert.Equal(1, rightCompleted);
            Assert.Equal(0, rootCompleted);
        }
        Assert.Equal(1, rootCompleted);
    }

    [Fact]
    public void ThrowingFailureCallbackStillNotifiesOtherUniqueObserversAndReleasesAllHolds()
    {
        var original = new InvalidOperationException("original shared observer failure");
        int root = 0, left = 0, right = 0, completed = 0;
        using (MessageApplyScope.Observe(() => true, _ => { root++; throw original; }, () => completed++))
        {
            MessageApplyScope.RetainedObservation captured;
            using (MessageApplyScope.Observe(() => true, _ => left++, () => completed++))
                captured = MessageApplyScope.RetainCurrent();
            using (MessageApplyScope.Observe(() => true, _ => right++, () => completed++))
            using (captured.Activate())
            {
                Assert.Same(original, Assert.Throws<InvalidOperationException>(() => MessageApplyScope.Fail("failure")));
                Assert.Equal(1, root);
                Assert.Equal(1, left);
                Assert.Equal(1, right);
            }
            captured.Complete();
        }
        Assert.Equal(3, completed);
    }

    [Fact]
    public void SharedGraphStillShortCircuitsFalseSourceBeforeOtherValidators()
    {
        bool valid = true;
        int root = 0, left = 0, right = 0;
        using (MessageApplyScope.Observe(() => { root++; return valid; }, _ => { }, () => { }))
        {
            MessageApplyScope.RetainedObservation captured;
            using (MessageApplyScope.Observe(() => { left++; return true; }, _ => { }, () => { }))
                captured = MessageApplyScope.RetainCurrent();
            using (MessageApplyScope.Observe(() => { right++; return true; }, _ => { }, () => { }))
            using (captured.Activate())
            {
                root = left = right = 0;
                valid = false;
                Assert.False(MessageApplyScope.IsCurrent);
                Assert.Equal(1, root);
                Assert.Equal(1, left);
                Assert.Equal(0, right);
            }
            captured.Complete();
        }
    }

    private static void WithAliasedActivations(int depth, Action action)
    {
        if (depth == 0) { action(); return; }
        using var pending = MessageApplyScope.RetainCurrent();
        using (pending.Activate()) WithAliasedActivations(depth - 1, action);
        pending.Complete();
    }

    [Fact]
    public void DeepDistinctSharedGraphHasExactUniqueCoverageForMissingDomainAndCallbacks()
    {
        var domain = new object();
        int validations = 0, failures = 0;
        MessageApplyReceipt Node(MessageApplyReceipt? source = null, MessageApplyReceipt? parent = null,
            object? ownDomain = null) => new(_ => failures++, () => { }, () => { validations++; return true; },
                ownDomain, parent, source);
        var graph = Node(ownDomain: domain);
        const int depth = 1024;
        for (int i = 0; i < depth; i++) graph = Node(Node(graph), Node(parent: graph));
        const int uniqueNodes = depth * 3 + 1;
        Assert.False(graph.Observes(new object(), out var visited));
        Assert.Equal(uniqueNodes, visited);
        Assert.True(graph.Observes(domain));
        Assert.False(graph.Observes(null, out visited));
        Assert.Equal(0, visited);
        Assert.True(graph.IsCurrent);
        Assert.Equal(uniqueNodes, validations);
        graph.Fail("shared graph fault");
        Assert.Equal(uniqueNodes, failures);
    }

    [Fact]
    public void NonSharedFalseBranchStillShortCircuitsAmbientValidation()
    {
        int source = 0, ambient = 0;
        var sourceReceipt = new MessageApplyReceipt(_ => { }, () => { }, () => { source++; return false; });
        var ambientReceipt = new MessageApplyReceipt(_ => { }, () => { }, () => { ambient++; return true; });
        var graph = new MessageApplyReceipt(null, () => { }, null, parent: ambientReceipt, source: sourceReceipt);
        Assert.False(graph.IsCurrent);
        Assert.Equal(1, source);
        Assert.Equal(0, ambient);
    }

    [Fact]
    public void SharedValidatorExceptionPreservesOriginalAndShortCircuitWithoutLosingLifetime()
    {
        var original = new InvalidOperationException("unique source validation failure");
        bool throwing = false;
        int validations = 0, completed = 0;
        using (MessageApplyScope.Observe(() =>
        {
            validations++;
            if (throwing) throw original;
            return true;
        }, _ => { }, () => completed++))
            WithAliasedActivations(8, () =>
            {
                throwing = true;
                validations = 0;
                Assert.Same(original, Assert.Throws<InvalidOperationException>(() => _ = MessageApplyScope.IsCurrent));
                Assert.Equal(1, validations);
            });
        Assert.Equal(1, completed);
    }

    [Fact]
    public void SecondaryObserverFailureIsLoggedWithoutReplacingFirstExceptionOrStrandingHolds()
    {
        var first = new InvalidOperationException("first observer callback failure");
        var secondary = new InvalidOperationException("secondary observer callback failure");
        const string reason = "original retained apply failure #42";
        int firstNotified = 0, secondaryNotified = 0, completed = 0;
        var sink = new CaptureSink();
        var previousLogger = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ => { secondaryNotified++; throw secondary; }, () => completed++))
            using (MessageApplyScope.Observe(() => true, _ => { firstNotified++; throw first; }, () => completed++))
                pending = MessageApplyScope.RetainCurrent();
            using (pending.Activate())
            {
                Assert.Same(first, Assert.Throws<InvalidOperationException>(() => MessageApplyScope.Fail(reason)));
                Assert.Equal(1, firstNotified);
                Assert.Equal(1, secondaryNotified);
                Assert.Equal(0, completed);
            }
            pending.Complete();
            Assert.Equal(2, completed);
            Assert.False(pending.IsCurrent);
            var entry = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Error, entry.Level);
            Assert.Same(secondary, entry.Exception);
            Assert.Equal(reason, Assert.IsType<ScalarValue>(entry.Properties["ApplyFailureReason"]).Value);
            Assert.Contains("failure callback", entry.RenderMessage());
        }
        finally { Log.Logger = previousLogger; }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
