using Common.Messaging;
using Common.Session;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class CampaignApplyCompletionTests : IDisposable
{
    private sealed record Result : ICampaignAuthorityScopedMessage;
    private readonly int previous = GameThread.Instance.GameThreadId;
    private readonly GameThread.QueueContext queue = new();
    private readonly IDisposable queueScope;
    private readonly SessionIdentity identity = new();
    private readonly MessageBroker broker = new();
    private readonly AuthorityLease lease = new("room", "writer", "b", 3);
    private CampaignApplyCompletion Completion => CampaignApplyCompletion.For(identity);
    public CampaignApplyCompletionTests()
    {
        queueScope = GameThread.ActivateQueue(queue);
        GameThread.Instance.MarkGameThread();
        Assert.True(identity.TryApply(lease, "a"));
        CampaignApplyCompletion.Attach(broker, identity);
    }
    public void Dispose()
    {
        GameThread.Instance.DiscardQueuedActions();
        queueScope.Dispose();
        GameThread.Instance.RestoreGameThread(previous);
        broker.Dispose();
    }
    [Fact]
    public void NestedEnqueueRemainsPendingAfterOuterPublishAndFirstFrame()
    {
        bool applied = false;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => applied = true));
        broker.Subscribe<Result>(Handle);
        broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.False(applied);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.True(applied);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void BrokerExceptionStillReachesOtherSubscribersButCannotAcknowledge()
    {
        int delivered = 0;
        void Fail(MessagePayload<Result> _) { GC.KeepAlive(this); throw new InvalidOperationException("native failure"); }
        void Good(MessagePayload<Result> _) => delivered++;
        broker.Subscribe<Result>(Fail); broker.Subscribe<Result>(Good);
        broker.Publish(this, new Result());
        Assert.Equal(1, delivered);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("native failure", reason);
        // Changing writer/epoch does not launder an earlier apply fault in the same room.
        Assert.False(Completion.CanAcknowledgeThrough(new AuthorityLease("room", "next", "a", 4), out _));
        Assert.True(Completion.CanAcknowledgeThrough(new AuthorityLease("other", "next", "a", 4), out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Fail); GC.KeepAlive((Action<MessagePayload<Result>>)Good);
    }
    [Fact]
    public void RunSafeExceptionCannotBecomeSuccessfulCompletion()
    {
        void Handle(MessagePayload<Result> _) { GC.KeepAlive(this); GameThread.EnqueueSafe(() => throw new InvalidOperationException("queued failure")); }
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("queued failure", reason);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void DiscardedApplyIsFaultedRatherThanSuccessfulOrPermanentlyPending()
    {
        void Handle(MessagePayload<Result> _) { GC.KeepAlive(this); GameThread.EnqueueSafe(() => throw new Exception("must not execute")); }
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        GameThread.Instance.DiscardQueuedActions();
        GameThread.Instance.DiscardQueuedActions();
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("fault", reason);
        Assert.DoesNotContain("pending", reason);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void MissingSubscriberDoesNotCertifyAnApply()
    {
        broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("No subscriber", reason);
    }
    [Fact]
    public void OffThreadRunAndInlineRunKeepCompletionThroughTheActualApply()
    {
        bool applied = false;
        void Handle(MessagePayload<Result> _) => GameThread.RunSafe(() => GameThread.RunSafe(() => applied = true));
        broker.Subscribe<Result>(Handle);
        Task.Factory.StartNew(() => broker.Publish(this, new Result()), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        Assert.False(applied);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.True(applied);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void PreCanceledQueueMarksTheReceiptFailed()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        void Handle(MessagePayload<Result> _)
        {
            using (GameThread.ActivateCancellation(cancellation.Token)) GameThread.EnqueueSafe(() => { });
        }
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("canceled", reason);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void NonCriticalNestedEventWithoutSubscribersIsNotAnApplyFailure()
    {
        void Handle(MessagePayload<Result> _) => broker.Publish(this, new LocalEvent());
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    private sealed record LocalEvent : IMessage;
    [Fact]
    public void LeaseChangeBeforeQueuedApplyCancelsItAndRetainsTheSourceFault()
    {
        bool applied = false;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => applied = true);
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        var paused = new AuthorityLease("room", "paused", "", 4, true);
        Assert.True(identity.TryApply(paused, "a"));
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.False(applied);
        Assert.False(Completion.CanAcknowledgeThrough(paused, out var reason));
        Assert.Contains("source lease changed", reason);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void ExplicitCheckpointSourceMustBeConfirmedAndExact()
    {
        var other = new AuthorityLease("room", "other", "a", 4);
        Assert.Throws<InvalidOperationException>(() => Completion.ObserveSource(typeof(Result), other));
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(other, out _));
    }
    [Fact]
    public void ProgressFailureNeverHoldsCompletionGateWhileWaitingForSessionGate()
    {
        var inner = new SessionIdentity(); Assert.True(inner.TryApply(lease, "a"));
        using var sessionHeld = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var getterEntered = new ManualResetEventSlim();
        var proxy = new Moq.Mock<ISessionIdentity>();
        proxy.SetupGet(value => value.CurrentLease).Returns(() => { getterEntered.Set(); return inner.CurrentLease; });
        var observed = CampaignApplyCompletion.For(proxy.Object);
        observed.Progressed += () => throw new InvalidOperationException("notification failure");
        var holder = Task.Factory.StartNew(() => inner.TryExecuteWithLease(lease, () =>
        { sessionHeld.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? notifier = null; Task<bool>? probe = null;
        try
        {
            Assert.True(sessionHeld.Wait(TimeSpan.FromSeconds(5)));
            notifier = Task.Factory.StartNew(() => observed.RecordFailure(lease, "initial failure"), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(getterEntered.Wait(TimeSpan.FromSeconds(5)));
            probe = Task.Factory.StartNew(() => observed.CanAcknowledgeThrough(lease, out _), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(probe.Wait(TimeSpan.FromSeconds(1)), "Completion gate was held while the session gate was blocked.");
            Assert.False(probe.Result);
        }
        finally
        {
            release.Set(); Assert.True(holder.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(notifier == null || notifier.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(probe == null || probe.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
