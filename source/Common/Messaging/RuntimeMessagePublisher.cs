using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Common.Messaging;

/// <summary>Dispatches a validated deserialized message to its concrete typed subscribers.</summary>
public static class RuntimeMessagePublisher
{
    private static readonly ConcurrentDictionary<Type, Action<IMessageBroker, object, IMessage>> Publishers = new();

    public static void PublishRuntime(this IMessageBroker broker, object source, IMessage message)
    {
        if (broker == null) throw new ArgumentNullException(nameof(broker));
        if (message == null) throw new ArgumentNullException(nameof(message));
        Publishers.GetOrAdd(message.GetType(), CreatePublisher)(broker, source, message);
    }

    private static Action<IMessageBroker, object, IMessage> CreatePublisher(Type messageType)
    {
        var broker = Expression.Parameter(typeof(IMessageBroker), "broker");
        var source = Expression.Parameter(typeof(object), "source");
        var message = Expression.Parameter(typeof(IMessage), "message");
        var publish = typeof(IMessageBroker).GetMethod(nameof(IMessageBroker.Publish)).MakeGenericMethod(messageType);
        return Expression.Lambda<Action<IMessageBroker, object, IMessage>>(
            Expression.Call(broker, publish, source, Expression.Convert(message, messageType)), broker, source, message).Compile();
    }
}
