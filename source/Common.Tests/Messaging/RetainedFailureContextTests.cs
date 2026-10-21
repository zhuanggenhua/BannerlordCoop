using Common.Logging;
using Common.Messaging;
using System.Runtime.CompilerServices;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class RetainedFailureContextTests
{
    [Fact]
    public void SameSourceAndAmbientAreNotNotifiedAgainAtActivationDispose()
    {
        int notified = 0, completed = 0;
        using (MessageApplyScope.Observe(() => true, _ => notified++, () => completed++))
        {
            var pending = MessageApplyScope.RetainCurrent();
            using (pending.Activate()) pending.Complete("one rejected record");
            pending.Complete("one rejected record");
            Assert.Equal(1, notified);
            Assert.Equal(0, completed);
        }
        Assert.Equal(1, completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctBranchesReceiveOneFailureEachAndSharedRootOnlyOnce(bool extraOnCapturedBranch)
    {
        int root = 0, left = 0, right = 0, extra = 0, completed = 0;
        using (MessageApplyScope.Observe(() => true, _ => root++, () => completed++))
        {
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ => left++, () => completed++))
            {
                if (extraOnCapturedBranch)
                    using (MessageApplyScope.Observe(() => true, _ => extra++, () => completed++))
                        pending = MessageApplyScope.RetainCurrent();
                else
                pending = MessageApplyScope.RetainCurrent();
            }
            using (MessageApplyScope.Observe(() => true, _ => right++, () => completed++))
            {
                if (!extraOnCapturedBranch)
                    using (MessageApplyScope.Observe(() => true, _ => extra++, () => completed++))
                    using (pending.Activate()) pending.Complete("one shared failure");
                else
                    using (pending.Activate()) pending.Complete("one shared failure");
            }
            Assert.Equal(1, root);
            Assert.Equal(1, left);
            Assert.Equal(1, right);
            Assert.Equal(1, extra);
        }
        Assert.Equal(4, completed);
    }

    [Fact]
    public void NestedActivationsAndCallbackReentryShareOneFailureIdentity()
    {
        int notified = 0, completed = 0;
        IDisposable second = null;
        using (MessageApplyScope.Observe(() => true, _ =>
        {
            if (++notified == 1) second.Dispose();
        }, () => completed++))
        {
            var pending = MessageApplyScope.RetainCurrent();
            using (pending.Activate())
            {
                second = pending.Activate();
                pending.Complete("reentrant failure");
                second.Dispose();
            }
            Assert.Equal(1, notified);
        }
        Assert.Equal(1, completed);
    }

    [Fact]
    public void SameTextDoesNotCombineIndependentOwnersOrExplicitFailures()
    {
        int notified = 0, completed = 0;
        using (MessageApplyScope.Observe(() => true, _ => notified++, () => completed++))
        {
            var first = MessageApplyScope.RetainCurrent();
            var second = MessageApplyScope.RetainCurrent();
            using (first.Activate()) first.Complete("identical text");
            using (second.Activate()) second.Complete("identical text");
            MessageApplyScope.Fail("identical text");
            MessageApplyScope.Fail("identical text");
            Assert.Equal(4, notified);
        }
        Assert.Equal(1, completed);
    }

    [Fact]
    public void ReleaseExceptionCannotReplaceOriginalFailureCallbackException()
    {
        var original = new InvalidOperationException("first callback failure");
        var secondary = new InvalidOperationException("completion release failure");
        int notified = 0, completed = 0;
        string firstStack = null;
        var output = new List<string>();
        Action<string> capture = output.Add;
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ =>
            {
                notified++;
                try { ThrowFromObserver(original); }
                catch { firstStack = original.StackTrace; throw; }
            }, () => { completed++; throw secondary; })) pending = MessageApplyScope.RetainCurrent();
            var observed = Assert.Throws<InvalidOperationException>(() =>
            {
                using (pending.Activate()) pending.Complete("native apply rejected");
            });
            Assert.Same(original, observed);
            Assert.Contains(nameof(ThrowFromObserver), observed.StackTrace);
            Assert.StartsWith(firstStack, observed.StackTrace);
            Assert.Equal(1, notified);
            Assert.Equal(1, completed);
            Assert.Contains(output, line => line.Contains(secondary.Message) && line.Contains("native apply rejected"));
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    [Fact]
    public void DistinctAmbientExceptionCannotReplaceTheCapturedOriginal()
    {
        var original = new InvalidOperationException("captured first callback failure");
        var secondary = new InvalidOperationException("distinct ambient callback failure");
        int sourceNotified = 0, ambientNotified = 0, completed = 0;
        var output = new List<string>();
        Action<string> capture = output.Add;
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ => { sourceNotified++; ThrowFromObserver(original); }, () => completed++))
                pending = MessageApplyScope.RetainCurrent();
            using (MessageApplyScope.Observe(() => true, _ => { ambientNotified++; throw secondary; }, () => completed++))
            {
                var observed = Assert.Throws<InvalidOperationException>(() =>
                {
                    using (pending.Activate()) pending.Complete("shared rejection reason");
                });
                Assert.Same(original, observed);
                Assert.Contains(nameof(ThrowFromObserver), observed.StackTrace);
                Assert.Equal(1, sourceNotified);
                Assert.Equal(1, ambientNotified);
            }
            Assert.Equal(2, completed);
            Assert.Contains(output, line => line.Contains(secondary.Message) && line.Contains("shared rejection reason"));
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromObserver(Exception error) => throw error;

    [Fact]
    public void ConcurrentActivationsNotifySharedRootOnceAndDistinctAmbientObserversOnce()
    {
        int root = 0, ambient = 0, completed = 0;
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);
        using var disposed = new CountdownEvent(2);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using (MessageApplyScope.Observe(() => true, _ =>
        {
            Interlocked.Increment(ref root);
            release.Set();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)), "Other propagation cannot be blocked by a user callback holding the failure-context lock.");
        }, () => Interlocked.Increment(ref completed)))
        {
            var pending = MessageApplyScope.RetainCurrent();
            var bridge = MessageApplyScope.RetainCurrent();
            var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
            {
                try
                {
                    using (bridge.Activate())
                    using (MessageApplyScope.Observe(() => true, _ => Interlocked.Increment(ref ambient), () => Interlocked.Increment(ref completed)))
                    using (pending.Activate())
                    {
                        ready.Signal();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    }
                }
                catch (Exception error) { errors.Enqueue(error); }
                finally { disposed.Signal(); }
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            try
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
                pending.Complete("one concurrently activated pending record");
            }
            finally
            {
                release.Set();
                foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
                bridge.Complete();
            }
            Assert.Empty(errors);
            Assert.Equal(1, root);
            Assert.Equal(2, ambient);
        }
        Assert.Equal(3, completed);
    }

    [Fact]
    public void CancelledQueuedDescendantReusesOwnerFailureAndStillNotifiesDistinctAmbient()
    {
        int previous = GameThread.Instance.GameThreadId;
        var queue = new GameThread.QueueContext();
        using var queueScope = GameThread.ActivateQueue(queue);
        GameThread.Instance.MarkGameThread();
        try
        {
            int source = 0, ambient = 0, completed = 0;
            bool applied = false;
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ => source++, () => completed++))
                pending = MessageApplyScope.RetainCurrent();
            using (MessageApplyScope.Observe(() => true, _ => ambient++, () => completed++))
            using (pending.Activate()) GameThread.EnqueueSafe(() => applied = true);
            pending.Complete("cancelled before actual queue apply");
            GameThread.Instance.Update(TimeSpan.Zero);
            Assert.False(applied);
            Assert.Equal(1, source);
            Assert.Equal(1, ambient);
            Assert.Equal(2, completed);
        }
        finally
        {
            GameThread.Instance.DiscardQueuedActions();
            GameThread.Instance.RestoreGameThread(previous);
        }
    }

    [Fact]
    public void ActualQueuedActionPrimarySurvivesFailureAndReleaseCallbacks()
    {
        int previous = GameThread.Instance.GameThreadId;
        var queue = new GameThread.QueueContext();
        using var queueScope = GameThread.ActivateQueue(queue);
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        var primary = new InvalidOperationException("real original action failure");
        string firstStack = null;
        var callback = new MarkedException("failure callback secondary");
        var completion = new MarkedException("release callback secondary");
        var output = new List<string>();
        Action<string> capture = output.Add;
        OutputSinkManager.AddLogCallback(capture);
        GameThread.Instance.MarkGameThread();
        try
        {
            int notified = 0, completed = 0;
            GameThread.QueuedAction task;
            using (MessageApplyScope.Observe(() => true, _ => { notified++; throw callback; },
                () => { completed++; throw completion; }))
            {
                task = new GameThread.QueuedAction(() =>
                {
                    try { ThrowFromAction(primary); }
                    catch { firstStack = primary.StackTrace; throw; }
                }, signal, "actual primary action", CancellationToken.None);
                lock (queue.gate) queue.queue.Enqueue(task);
            }
            var observed = Assert.Throws<InvalidOperationException>(() => GameThread.Instance.Update(TimeSpan.Zero));
            Assert.Same(primary, observed);
            Assert.Contains(nameof(ThrowFromAction), observed.StackTrace);
            Assert.StartsWith(firstStack, observed.StackTrace);
            Assert.True(signal.WaitOne(0));
            Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => GameThread.WaitForBlockingCompletion(task, CancellationToken.None)));
            Assert.Equal(1, notified);
            Assert.Equal(1, completed);
            Assert.True(callback.RenderCount > 0);
            Assert.True(completion.RenderCount > 0);
            Assert.Single(output.Where(line => line.Contains(callback.Marker)));
            Assert.Single(output.Where(line => line.Contains(completion.Marker)));
        }
        finally
        {
            OutputSinkManager.RemoveLogCallback(capture);
            GameThread.Instance.DiscardQueuedActions();
            GameThread.Instance.RestoreGameThread(previous);
        }
    }

    private sealed class MarkedException : InvalidOperationException
    {
        internal readonly string Marker = Guid.NewGuid().ToString("N");
        internal int RenderCount;
        internal MarkedException(string message) : base(message) { }
        public override string ToString()
        { RenderCount++; return $"instance:{Marker} {base.ToString()}"; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromAction(Exception error) => throw error;

    [Fact]
    public void AlreadyRunningActionKeepsItsIndependentPrimaryAfterOwnerRejection()
    {
        int previous = GameThread.Instance.GameThreadId;
        var queue = new GameThread.QueueContext();
        using var queueScope = GameThread.ActivateQueue(queue);
        using var actionStarted = new ManualResetEventSlim(false);
        using var otherActive = new ManualResetEventSlim(false);
        using var rejected = new ManualResetEventSlim(false);
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        var primary = new InvalidOperationException("independent already-running native action");
        var ownerError = new MarkedException("prior owner rejection callback");
        var releaseError = new MarkedException("final release callback");
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<string> capture = output.Enqueue;
        OutputSinkManager.AddLogCallback(capture);
        GameThread.Instance.MarkGameThread();
        Thread thread = null;
        Exception otherObserved = null;
        int notified = 0, completed = 0;
        string firstStack = null;
        try
        {
            MessageApplyScope.RetainedObservation pending;
            using (MessageApplyScope.Observe(() => true, _ =>
            {
                Interlocked.Increment(ref notified);
                throw ownerError;
            }, () => { Interlocked.Increment(ref completed); throw releaseError; }))
                pending = MessageApplyScope.RetainCurrent();
            GameThread.QueuedAction task;
            using (pending.Activate())
            {
                task = new GameThread.QueuedAction(() =>
                {
                    actionStarted.Set();
                    Assert.True(rejected.Wait(TimeSpan.FromSeconds(5)));
                    try { ThrowFromAction(primary); }
                    catch { firstStack = primary.StackTrace; throw; }
                }, signal, "running action versus owner reject", CancellationToken.None);
                lock (queue.gate) queue.queue.Enqueue(task);
            }
            thread = new Thread(() =>
            {
                try
                {
                    using (pending.Activate())
                    {
                        otherActive.Set();
                        Assert.True(actionStarted.Wait(TimeSpan.FromSeconds(5)));
                        try { pending.Complete("prior owner rejection"); }
                        finally { rejected.Set(); }
                    }
                }
                catch (Exception error) { otherObserved = error; rejected.Set(); }
            });
            thread.Start();
            Assert.True(otherActive.Wait(TimeSpan.FromSeconds(5)));
            var observed = Assert.Throws<InvalidOperationException>(() => GameThread.Instance.Update(TimeSpan.Zero));
            Assert.Same(primary, observed);
            Assert.StartsWith(firstStack, observed.StackTrace);
            Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => GameThread.WaitForBlockingCompletion(task, CancellationToken.None)));
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Same(ownerError, otherObserved);
            Assert.Equal(2, notified); // Owner rejection and the independent native exception are distinct events.
            Assert.Equal(1, completed);
            Assert.True(ownerError.RenderCount > 0);
            Assert.True(releaseError.RenderCount > 0);
            Assert.Contains(output, line => line.Contains(ownerError.Marker));
            Assert.Contains(output, line => line.Contains(releaseError.Marker));
        }
        finally
        {
            actionStarted.Set();
            rejected.Set();
            if (thread != null) Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            OutputSinkManager.RemoveLogCallback(capture);
            GameThread.Instance.DiscardQueuedActions();
            GameThread.Instance.RestoreGameThread(previous);
        }
    }
}
