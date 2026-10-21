using Common.Messaging;
using Common.Session;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class RetainedApplyObservationTests : IDisposable
{
    private sealed record Result : ICampaignAuthorityScopedMessage;
    private readonly int previous = GameThread.Instance.GameThreadId;
    private readonly GameThread.QueueContext queue = new();
    private readonly IDisposable queueScope;
    private readonly SessionIdentity identity = new();
    private readonly MessageBroker broker = new();
    private readonly AuthorityLease lease = new("room", "writer", "b", 3);
    private readonly CampaignApplyCompletion.Binding binding;
    private CampaignApplyCompletion Completion => CampaignApplyCompletion.For(identity);

    public RetainedApplyObservationTests()
    {
        queueScope = GameThread.ActivateQueue(queue);
        GameThread.Instance.MarkGameThread();
        Assert.True(identity.TryApply(lease, "a"));
        binding = CampaignApplyCompletion.Attach(broker, identity);
    }

    [Fact]
    public void NoObservationCannotProduceARetainedSource()
    {
        Assert.Null(MessageApplyScope.RetainCurrent());
    }

    [Fact]
    public void PendingHandleOutlivesOriginalScopeAndCompletesOnlyOnce()
    {
        int completed = 0;
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => true, _ => { }, () => completed++))
            pending = MessageApplyScope.RetainCurrent();
        Assert.True(pending.IsCurrent);
        Assert.Equal(0, completed);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        pending.Complete();
        pending.Complete();
        pending.Dispose();
        Assert.False(pending.IsCurrent);
        Assert.Equal(1, completed);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        Assert.Throws<ObjectDisposedException>(() => pending.Activate());
    }

    [Fact]
    public void ActivationRetainsAppliedScopeAndItsQueueDescendantsAfterOwnerCompletes()
    {
        int completed = 0;
        bool applied = false;
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => true, _ => { }, () => completed++))
            pending = MessageApplyScope.RetainCurrent();
        using (pending.Activate())
        {
            pending.Complete();
            Assert.False(pending.IsCurrent);
            Assert.Equal(0, completed);
            GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => applied = true));
        }
        Assert.Equal(0, completed);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out _));
        Drain();
        Assert.False(applied);
        Drain();
        Assert.True(applied);
        Assert.Equal(1, completed);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
    }

    [Fact]
    public void DisposeOfUnconsumedPendingRecordFaultsInsteadOfAcknowledgingApply()
    {
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result))) pending = MessageApplyScope.RetainCurrent();
        pending.Dispose();
        pending.Dispose();
        pending.Complete();
        Assert.False(pending.IsCurrent);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("disposed", reason);
        Assert.Throws<ObjectDisposedException>(() => pending.Activate());
    }

    [Fact]
    public void StalePendingSourceRefusesActivationAndFaultsOriginalCampaign()
    {
        bool sourceCurrent = true;
        int completed = 0;
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => sourceCurrent, _ => { }, () => completed++))
            pending = MessageApplyScope.RetainCurrent();
        sourceCurrent = false;
        Assert.False(pending.IsCurrent);
        Assert.Throws<OperationCanceledException>(() => pending.Activate());
        Assert.False(pending.IsCurrent);
        Assert.Equal(1, completed);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("source", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingValidatorSettlesPendingAndPreservesOriginalException(bool activate)
    {
        var original = new InvalidOperationException("retained source validation failure");
        int completed = 0;
        int campaignNotifications = 0;
        Completion.Progressed += () => campaignNotifications++;
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => throw original,
            _ => throw new InvalidOperationException("secondary failure callback"), () => completed++))
            pending = MessageApplyScope.RetainCurrent();
        var observed = activate ? Assert.Throws<InvalidOperationException>(() => pending.Activate())
            : Assert.Throws<InvalidOperationException>(() => _ = pending.IsCurrent);
        Assert.Same(original, observed);
        Assert.False(pending.IsCurrent);
        Assert.Equal(1, completed);
        Assert.Equal(2, campaignNotifications);
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Contains("retained source validation failure", reason);
        pending.Dispose();
        Assert.Equal(1, completed);
    }

    [Fact]
    public void CapturedCampaignDomainSurvivesActivationInsideAnUnrelatedObservation()
    {
        MessageApplyScope.RetainedObservation pending;
        using (Completion.ObserveSource(typeof(Result), lease))
        using (MessageApplyScope.Observe(() => true, _ => { }, () => { }))
            pending = MessageApplyScope.RetainCurrent();
        int ambientCompleted = 0;
        void Handle(MessagePayload<Result> _) { GC.KeepAlive(this); GameThread.EnqueueSafe(() => { }); }
        broker.Subscribe<Result>(Handle);
        using (MessageApplyScope.Observe(() => true, _ => { }, () => ambientCompleted++))
        using (pending.Activate()) broker.Publish(this, new Result());
        pending.Complete();
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Equal("pending classified applies: 1", reason);
        Assert.Equal(0, ambientCompleted);
        Drain();
        Assert.Equal(1, ambientCompleted);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

    [Fact]
    public void CapturedDistinctSourceDoesNotSuppressAmbientCampaignObservation()
    {
        MessageApplyScope.RetainedObservation pending;
        using (MessageApplyScope.Observe(() => true, _ => { }, () => { }))
            pending = MessageApplyScope.RetainCurrent();
        void Handle(MessagePayload<Result> _) { GC.KeepAlive(this); GameThread.EnqueueSafe(() => { }); }
        broker.Subscribe<Result>(Handle);
        using (pending.Activate()) broker.Publish(this, new Result());
        pending.Complete();
        Assert.False(Completion.CanAcknowledgeThrough(lease, out var reason));
        Assert.Equal("pending classified applies: 1", reason);
        Drain();
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }

    [Fact]
    public void CapturedLeaseChangeRefusesActualActivation()
    {
        MessageApplyScope.RetainedObservation pending;
        using (Completion.ObserveSource(typeof(Result), lease)) pending = MessageApplyScope.RetainCurrent();
        var next = new AuthorityLease("room", "writer-a", "a", 4);
        Assert.True(identity.TryApply(next, "a"));
        Assert.False(pending.IsCurrent);
        Assert.Throws<OperationCanceledException>(() => pending.Activate());
        Assert.False(pending.IsCurrent);
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(next, out _));
    }

    [Fact]
    public void QueuedDescendantStillValidatesSourceAfterThePendingOwnerCompleted()
    {
        bool sourceCurrent = true;
        bool applied = false;
        int completed = 0;
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => sourceCurrent, _ => { }, () => completed++))
            pending = MessageApplyScope.RetainCurrent();
        using (pending.Activate()) GameThread.EnqueueSafe(() => applied = true);
        pending.Complete();
        sourceCurrent = false;
        Assert.Equal(0, completed);
        Drain();
        Assert.False(applied);
        Assert.Equal(1, completed);
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(lease, out _));
    }

    [Fact]
    public void SourceValidationRunsOutsideTheHandleLockAndCannotReactivateAClosedOwner()
    {
        bool closeDuringValidation = false;
        int completed = 0;
        MessageApplyScope.RetainedObservation pending = null;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() =>
        {
            if (closeDuringValidation)
            {
                var closing = Task.Run(() => pending.Complete());
                Assert.True(closing.Wait(TimeSpan.FromSeconds(2)), "The source callback must not hold the handle state lock.");
            }
            return true;
        }, _ => { }, () => completed++)) pending = MessageApplyScope.RetainCurrent();
        closeDuringValidation = true;
        Assert.Throws<ObjectDisposedException>(() => pending.Activate());
        Assert.False(pending.IsCurrent);
        Assert.Equal(1, completed);
        Assert.True(Completion.CanAcknowledgeThrough(lease, out _));
    }

    [Fact]
    public void ClosedHandleDoesNotInvokeItsFormerSourceValidator()
    {
        bool closed = false;
        MessageApplyScope.RetainedObservation pending;
        using (MessageApplyScope.Observe(() => closed ? throw new Exception("closed source must not be queried") : true,
            _ => { }, () => { })) pending = MessageApplyScope.RetainCurrent();
        pending.Complete();
        closed = true;
        Assert.False(pending.IsCurrent);
        Assert.Throws<ObjectDisposedException>(() => pending.Activate());
    }

    [Fact]
    public void ThrowingCompletionCallbackCannotCertifyParentCampaignSuccess()
    {
        var original = new InvalidOperationException("retained completion callback failure");
        MessageApplyScope.RetainedObservation pending;
        using (Completion.Observe(typeof(Result)))
        using (MessageApplyScope.Observe(() => true, _ => { }, () => throw original))
            pending = MessageApplyScope.RetainCurrent();
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => pending.Complete()));
        Assert.False(pending.IsCurrent);
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(lease, out var reason));
        Assert.Contains("retained completion callback failure", reason);
        pending.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPendingOwnerCannotCertifyItsAmbientCampaignOrRunQueuedDescendants(bool failAfterActivationEnds)
    {
        MessageApplyScope.RetainedObservation pending;
        using (MessageApplyScope.Observe(() => true, _ => { }, () => { })) pending = MessageApplyScope.RetainCurrent();
        bool applied = false;
        using (Completion.Observe(typeof(Result)))
        using (pending.Activate())
        {
            GameThread.EnqueueSafe(() => applied = true);
            if (!failAfterActivationEnds) pending.Complete("pending record cancelled before apply completed");
        }
        if (failAfterActivationEnds) pending.Complete("pending record cancelled before apply completed");
        Drain();
        Assert.False(applied);
        Assert.False(pending.IsCurrent);
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(lease, out _));
    }

    [Fact]
    public void FailedOwnerInsideActiveApplyFaultsTheAmbientCampaignEvenWithoutQueuedWork()
    {
        MessageApplyScope.RetainedObservation pending;
        using (MessageApplyScope.Observe(() => true, _ => { }, () => { })) pending = MessageApplyScope.RetainCurrent();
        using (Completion.Observe(typeof(Result)))
        using (pending.Activate()) pending.Complete("native pending apply failed");
        Assert.Equal(CampaignApplyStatus.Faulted, Completion.GetStatusThrough(lease, out var reason));
        Assert.Contains("native pending apply failed", reason);
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
