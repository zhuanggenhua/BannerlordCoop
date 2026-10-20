using System;
using System.Threading;

namespace Common.Messaging;

/// <summary>Optional completion observation; it grants no execution or replay permission.</summary>
public interface IMessageApplyObserver
{
    MessageApplyReceipt Begin(Type messageType);
}

/// <summary>Idle registrations grant no execution; confirmed competing sessions are rejected.</summary>
public interface IMessageApplySessionObserver : IMessageApplyObserver
{
    bool IsConfirmed { get; }
    void RejectBinding(string reason);
}

/// <summary>One classified result and its synchronous/game-thread descendants.</summary>
public sealed class MessageApplyReceipt
{
    private readonly Action<string> failed;
    private readonly Action completed;
    private readonly Func<bool> isCurrent;
    private int outstanding = 1;
    internal MessageApplyReceipt(Action<string> failed, Action completed, Func<bool> isCurrent)
    { this.failed = failed; this.completed = completed; this.isCurrent = isCurrent; }
    internal bool IsCurrent => isCurrent();
    internal void Retain() => Interlocked.Increment(ref outstanding);
    internal void Release() { if (Interlocked.Decrement(ref outstanding) == 0) completed(); }
    internal void Fail(string reason) => failed(reason);
}

/// <summary>Thread-bound observation, explicitly retained by GameThread; not ExecutionContext authority.</summary>
public sealed class MessageApplyScope : IDisposable
{
    [ThreadStatic] private static MessageApplyReceipt current;
    private readonly MessageApplyReceipt previous;
    private MessageApplyReceipt receipt;
    private readonly bool release;
    private bool disposed;
    private MessageApplyScope(MessageApplyReceipt receipt, bool release)
    { previous = current; current = this.receipt = receipt; this.release = release; }
    internal static bool HasCurrent => current != null;
    internal static bool IsCurrent => current == null || current.IsCurrent;
    internal static MessageApplyScope Begin(IMessageApplyObserver observer, Type type) =>
        observer == null ? null : new MessageApplyScope(observer.Begin(type), true);
    internal static IDisposable Suspend() => new MessageApplyScope(null, false);
    internal static Continuation Capture() => current == null ? null : new Continuation(current);
    public static void Fail(string reason) => current?.Fail(reason);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var value = receipt;
        receipt = null; current = previous;
        if (release) value?.Release();
    }
    internal sealed class Continuation
    {
        private MessageApplyReceipt receipt;
        internal Continuation(MessageApplyReceipt receipt) { this.receipt = receipt; receipt.Retain(); }
        internal bool IsCurrent => receipt == null || receipt.IsCurrent;
        internal IDisposable Activate() => receipt == null ? null : new MessageApplyScope(receipt, false);
        internal void Complete(string failure = null)
        {
            var value = Interlocked.Exchange(ref receipt, null);
            if (value == null) return;
            if (failure != null) value.Fail(failure);
            value.Release();
        }
    }
}
