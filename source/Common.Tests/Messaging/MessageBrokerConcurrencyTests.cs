using Common.Messaging;

namespace Common.Tests.Messaging;

public class MessageBrokerConcurrencyTests
{
    private sealed record Event : IMessage;
    [Fact]
    public async Task ConcurrentSubscriptionMutationKeepsTheSharedTableUsableWithoutLockingInvocations()
    {
        using var broker = new MessageBroker();
        int received = 0;
        var handlers = Enumerable.Range(0, 128).Select(index =>
            (Action<MessagePayload<Event>>)(_ => { GC.KeepAlive(index); Interlocked.Increment(ref received); })).ToArray();
        await Task.WhenAll(handlers.Select(handler => Task.Run(() => broker.Subscribe(handler))))
            .WaitAsync(TimeSpan.FromSeconds(5));
        broker.Publish(this, new Event());
        Assert.Equal(handlers.Length, received);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int step = 0; step < 100; step++)
            {
                var handler = handlers[(worker * 31 + step) % handlers.Length];
                broker.Unsubscribe(handler);
                broker.Publish(this, new Event());
                broker.Subscribe(handler);
            }
        }))).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(handlers.Select(handler => Task.Run(() => broker.Unsubscribe(handler))))
            .WaitAsync(TimeSpan.FromSeconds(5));
        received = 0;
        broker.Publish(this, new Event());
        Assert.Equal(0, received);
        // Delegates remain weak in the broker; this array deliberately owns them for this test.
        GC.KeepAlive(handlers);
    }
}
