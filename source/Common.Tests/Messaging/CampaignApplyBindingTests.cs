using Common.Messaging;
using Common.Session;

namespace Common.Tests.Messaging;

[Collection(nameof(GameThreadCollection))]
public class CampaignApplyBindingTests : IDisposable
{
    private sealed record Result : ICampaignAuthorityScopedMessage;
    private readonly MessageBroker broker = new();
    private readonly GameThread.QueueContext queue = new();
    private readonly IDisposable queueScope;
    private readonly int previous = GameThread.Instance.GameThreadId;
    private readonly AuthorityLease lease = new("room", "writer", "a", 3);
    public CampaignApplyBindingTests()
    {
        queueScope = GameThread.ActivateQueue(queue);
        GameThread.Instance.MarkGameThread();
    }
    public void Dispose()
    {
        GameThread.Instance.DiscardQueuedActions();
        queueScope.Dispose();
        GameThread.Instance.RestoreGameThread(previous);
        broker.Dispose();
    }
    private SessionIdentity Confirmed()
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(lease, "a"));
        return identity;
    }
    [Fact]
    public void SharedSessionOwnersReleaseOnceAndLastDetachCancelsQueuedDescendants()
    {
        var identity = Confirmed();
        var first = CampaignApplyCompletion.Attach(broker, identity);
        var second = CampaignApplyCompletion.Attach(broker, identity);
        int calls = 0;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => calls++);
        broker.Subscribe<Result>(Handle);
        first.Dispose(); first.Dispose();
        broker.Publish(this, new Result());
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.Equal(1, calls);
        broker.Publish(this, new Result());
        second.Dispose(); second.Dispose();
        var next = Confirmed();
        using var binding = CampaignApplyCompletion.Attach(broker, next);
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.Equal(1, calls);
        Assert.Equal(CampaignApplyStatus.Faulted, CampaignApplyCompletion.For(identity).GetStatusThrough(lease, out _));
        Assert.True(CampaignApplyCompletion.For(next).CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void SameIdentityRebindingDoesNotReviveEarlierReceiptGeneration()
    {
        var identity = Confirmed();
        var old = CampaignApplyCompletion.Attach(broker, identity);
        int calls = 0;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => calls++);
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        old.Dispose();
        using var next = CampaignApplyCompletion.Attach(broker, identity);
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.Equal(0, calls);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void DifferentConfirmedSessionIsRejectedWhileFirstBindingRemainsActive()
    {
        using var first = CampaignApplyCompletion.Attach(broker, Confirmed());
        Assert.Throws<InvalidOperationException>(() => CampaignApplyCompletion.Attach(broker, Confirmed()));
        Assert.True(first.IsActive);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleRegistrationsPermitConstructionButNeverGuessCriticalMessageOwnership(bool confirmFirst)
    {
        var first = new SessionIdentity(); var second = new SessionIdentity();
        using var a = CampaignApplyCompletion.Attach(broker, first);
        using var b = CampaignApplyCompletion.Attach(broker, second);
        if (confirmFirst) Assert.True(first.TryApply(lease, "a"));
        int calls = 0;
        void Handle(MessagePayload<Result> _) => calls++;
        broker.Subscribe<Result>(Handle);
        Assert.Throws<InvalidOperationException>(() => broker.Publish(this, new Result()));
        Assert.Equal(0, calls);
        Assert.True(second.TryApply(lease, "a"));
        Assert.False(CampaignApplyCompletion.For(first).CanAcknowledgeThrough(lease, out _));
        Assert.False(CampaignApplyCompletion.For(second).CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
    [Fact]
    public void BrokerDisposalRevokesIgnoredTokensAndExplicitCheckpointDescendants()
    {
        var identity = Confirmed();
        var binding = CampaignApplyCompletion.Attach(broker, identity);
        int calls = 0;
        using (CampaignApplyCompletion.For(identity).ObserveSource(typeof(Result), lease))
            GameThread.EnqueueSafe(() => calls++);
        broker.Dispose();
        Assert.False(binding.IsActive);
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.Equal(0, calls);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        Assert.Throws<ObjectDisposedException>(() => binding.Observe(typeof(Result)));
        binding.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleUnconfirmedJoinRegistrationStillTracksItsActualApply(bool confirmBeforeApply)
    {
        var identity = new SessionIdentity();
        using var binding = CampaignApplyCompletion.Attach(broker, identity);
        bool applied = false;
        void Handle(MessagePayload<Result> _) => GameThread.EnqueueSafe(() => applied = true);
        broker.Subscribe<Result>(Handle); broker.Publish(this, new Result());
        if (!confirmBeforeApply) GameThread.Instance.Update(TimeSpan.Zero);
        Assert.True(identity.TryApply(lease, "a"));
        GameThread.Instance.Update(TimeSpan.Zero);
        Assert.Equal(!confirmBeforeApply, applied);
        Assert.Equal(!confirmBeforeApply, CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(lease, out _));
        GC.KeepAlive((Action<MessagePayload<Result>>)Handle);
    }
}
