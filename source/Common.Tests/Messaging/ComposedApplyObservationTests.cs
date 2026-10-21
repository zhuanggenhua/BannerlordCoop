using Common.Messaging;
using Common.Session;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class ComposedApplyObservationTests : IDisposable
{
    private sealed record Result : ICampaignAuthorityScopedMessage;
    private sealed record NestedResult : ICampaignAuthorityScopedMessage;
    private readonly int previous = GameThread.Instance.GameThreadId;
    private readonly GameThread.QueueContext queue = new();
    private readonly IDisposable queueScope;
    private readonly SessionIdentity identity = new();
    private readonly MessageBroker broker = new();
    private readonly AuthorityLease lease = new("room", "writer", "b", 3);
    private readonly CampaignApplyCompletion.Binding binding;
    private CampaignApplyCompletion Completion => CampaignApplyCompletion.For(identity);

    public ComposedApplyObservationTests()
    {
        queueScope = GameThread.ActivateQueue(queue);
        GameThread.Instance.MarkGameThread();
        Assert.True(identity.TryApply(lease, "a"));
        binding = CampaignApplyCompletion.Attach(broker, identity);
    }

    [Fact]
    public void CampaignParentRemainsPendingThroughDistinctChildAndBothQueueFrames()
    {
        var child = new Observer();
        bool applied = false;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Begin(child, typeof(Result)))
            GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => applied = true));
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(0, child.Completed);
        Drain();
        Assert.False(applied);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(0, child.Completed);
        Drain();
        Assert.True(applied);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(1, child.Completed);
    }

    [Fact]
    public void DistinctParentDoesNotSuppressRealCampaignObservationOrFinishBeforeItsDescendants()
    {
        var parent = new Observer();
        bool applied = false;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => applied = true));
        broker.Subscribe<Result>(Handle);
        using (MessageApplyScope.Begin(parent, typeof(Result))) broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(0, parent.Completed);
        Drain();
        Assert.False(applied);
        Assert.Equal(0, parent.Completed);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Drain();
        Assert.True(applied);
        Assert.Equal(1, parent.Completed);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherParentOrChildInvalidationCancelsQueuedApplyAndFaultsBoth(bool invalidateParent)
    {
        var child = new Observer();
        bool applied = false;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Begin(child, typeof(Result)))
            GameThread.EnqueueSafe(() => applied = true);
        if (invalidateParent) Assert.True(identity.TryApply(new AuthorityLease("room", "next", "a", 4), "a"));
        else child.Current = false;
        Drain();
        Assert.False(applied);
        Assert.Equal(1, child.Completed);
        Assert.Contains(child.Failures, value => value.Contains("source lease changed"));
        Assert.False(Completion.CanAcknowledgeThrough(identity.CurrentLease, out var reason));
        Assert.Contains("source lease changed", reason);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("discard")]
    public void DescendantFailureOrCancellationPropagatesToBothObservations(string failure)
    {
        var child = new Observer();
        using var canceled = new CancellationTokenSource();
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Begin(child, typeof(Result)))
        {
            if (failure == "cancel")
            {
                canceled.Cancel();
                using (GameThread.ActivateCancellation(canceled.Token)) GameThread.EnqueueSafe(() => { });
            }
            else GameThread.EnqueueSafe(() => throw new InvalidOperationException("native descendant failure"));
        }
        if (failure == "discard") GameThread.Instance.DiscardQueuedActions();
        else Drain();
        Assert.Equal(1, child.Completed);
        Assert.NotEmpty(child.Failures);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.DoesNotContain("pending", reason);
        if (failure == "throw") Assert.Contains("native descendant failure", reason);
    }

    [Fact]
    public void ClassifiedDescendantFailureInsideDistinctParentStillFaultsTheRealCampaignSession()
    {
        var parent = new Observer();
        void Handle(MessagePayload<Result> _)
        {
            GC.KeepAlive(this);
            GameThread.EnqueueSafe(() => throw new InvalidOperationException("classified nested failure"));
        }
        broker.Subscribe<Result>(Handle);
        using (MessageApplyScope.Begin(parent, typeof(Result))) broker.Publish(this, new Result());
        Drain();
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("classified nested failure", reason);
        Assert.Contains(parent.Failures, value => value.Contains("classified nested failure"));
        Assert.Equal(1, parent.Completed);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

    [Fact]
    public void PublicObservationCannotClaimCampaignCoverageAndCompletesAfterActualDescendants()
    {
        int completed = 0;
        var failures = new List<string>();
        void Handle(MessagePayload<Result> _)
        {
            GC.KeepAlive(this);
            GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => { }));
        }
        broker.Subscribe<Result>(Handle);
        using (MessageApplyScope.Observe(() => true, failures.Add, () => completed++))
            broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(0, completed);
        Drain();
        Assert.Equal(0, completed);
        Drain();
        Assert.Equal(1, completed);
        Assert.Empty(failures);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

    [Fact]
    public void ThrowingFailureObserverStillReleasesBothLifetimesAndRetainsCampaignFailure()
    {
        int completed = 0;
        MessageApplyScope.Continuation continuation;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => true,
            _ => throw new InvalidOperationException("observer callback failure"), () => completed++))
            continuation = MessageApplyScope.Capture();
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Throws<InvalidOperationException>(() => continuation.Complete("original descendant failure"));
        Assert.Equal(1, completed);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("original descendant failure", reason);
        continuation.Complete("already completed");
        Assert.Equal(1, completed);
    }

    [Fact]
    public void CampaignObservationBetweenTwoDistinctDomainsRetainsAndFaultsEveryAncestor()
    {
        var outer = new Observer();
        var inner = new Observer();
        using (MessageApplyScope.Begin(outer, typeof(Result)))
        using (Completion.ObserveSource(typeof(Result), lease))
        using (MessageApplyScope.Begin(inner, typeof(Result)))
            GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => throw new InvalidOperationException("deep descendant failure")));
        Assert.Equal(0, outer.Completed);
        Assert.Equal(0, inner.Completed);
        Drain();
        Assert.Equal(0, outer.Completed);
        Assert.Equal(0, inner.Completed);
        Drain();
        Assert.Equal(1, outer.Completed);
        Assert.Equal(1, inner.Completed);
        Assert.Contains(outer.Failures, value => value.Contains("deep descendant failure"));
        Assert.Contains(inner.Failures, value => value.Contains("deep descendant failure"));
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("deep descendant failure", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyObservedCampaignDomainDoesNotObserveNestedClassifiedResultsAgain(bool explicitSource)
    {
        var child = new Observer();
        void Handle(MessagePayload<Result> _) => broker.Publish(this, new NestedResult());
        void Nested(MessagePayload<NestedResult> _) { GC.KeepAlive(this); GameThread.EnqueueSafe(() => { }); }
        broker.Subscribe<Result>(Handle);
        broker.Subscribe<NestedResult>(Nested);
        using (explicitSource ? Completion.ObserveSource(typeof(Result), lease) : Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Begin(child, typeof(Result))) broker.Publish(this, new Result());
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Equal("pending classified applies: 1", reason);
        Drain();
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Equal(1, child.Completed);
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
        GC.KeepAlive((Action<MessagePayload<NestedResult>>)Nested);
    }

    private sealed class Observer : IMessageApplyObserver
    {
        internal bool Current = true;
        internal int Completed;
        internal readonly List<string> Failures = new();
        public MessageApplyReceipt Begin(Type messageType) => new(Failures.Add, () => Completed++, () => Current);
    }

    private static void Drain() => GameThread.Instance.Update(TimeSpan.Zero);
    public void Dispose()
    {
        GameThread.Instance.DiscardQueuedActions();
        binding.Dispose();
        broker.Dispose();
        queueScope.Dispose();
        GameThread.Instance.RestoreGameThread(previous);
    }
}
