using Common.Messaging;
using Common.Session;

namespace Common.Tests.Messaging;

public class CampaignApplyReentryTests
{
    private sealed record Result : ICampaignAuthorityScopedMessage;
    private static readonly AuthorityLease Lease = new("room", "writer", "a", 3);
    private static SessionIdentity Confirmed()
    {
        var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Lease, "a"));
        return identity;
    }

    [Fact]
    public void ReentrantIdleSessionAndNewSubscriberCannotReceiveAnAlreadyStartedCriticalResult()
    {
        using var broker = new MessageBroker();
        var original = Confirmed(); var next = new SessionIdentity();
        using var binding = CampaignApplyCompletion.Attach(broker, original);
        CampaignApplyCompletion.Binding? nextBinding = null;
        int firstCalls = 0, nextCalls = 0;
        void Next(MessagePayload<Result> _) => nextCalls++;
        void First(MessagePayload<Result> _)
        {
            firstCalls++;
            nextBinding = CampaignApplyCompletion.Attach(broker, next);
            broker.Subscribe<Result>(Next);
        }
        broker.Subscribe<Result>(First);
        try
        {
            var exception = Record.Exception(() => broker.Publish(this, new Result()));
            Assert.Equal(1, firstCalls);
            Assert.Equal(0, nextCalls);
            Assert.IsType<InvalidOperationException>(exception);
            Assert.False(CampaignApplyCompletion.For(original).CanAcknowledgeThrough(Lease, out _));
            Assert.True(next.TryApply(Lease, "a"));
            Assert.False(CampaignApplyCompletion.For(next).CanAcknowledgeThrough(Lease, out _));
        }
        finally { nextBinding?.Dispose(); }
        GC.KeepAlive((Action<MessagePayload<Result>>)First);
        GC.KeepAlive((Action<MessagePayload<Result>>)Next);
    }

    [Fact]
    public void LastBindingDetachedByFirstSubscriberStopsLaterSynchronousCriticalSubscribers()
    {
        using var broker = new MessageBroker();
        var original = Confirmed();
        using var binding = CampaignApplyCompletion.Attach(broker, original);
        int firstCalls = 0, laterCalls = 0;
        void First(MessagePayload<Result> _) { firstCalls++; binding.Dispose(); }
        void Later(MessagePayload<Result> _) => laterCalls++;
        broker.Subscribe<Result>(First); broker.Subscribe<Result>(Later);
        var exception = Record.Exception(() => broker.Publish(this, new Result()));
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, laterCalls);
        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(CampaignApplyStatus.Faulted, CampaignApplyCompletion.For(original).GetStatusThrough(Lease, out var reason));
        Assert.Contains("session binding", reason);
        Assert.DoesNotContain("pending", reason);
        GC.KeepAlive((Action<MessagePayload<Result>>)First);
        GC.KeepAlive((Action<MessagePayload<Result>>)Later);
    }
}
