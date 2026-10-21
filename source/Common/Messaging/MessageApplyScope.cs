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
    private readonly MessageApplyReceipt parent;
    internal object ObservationDomain { get; }
    private int outstanding = 1;
    internal MessageApplyReceipt(Action<string> failed, Action completed, Func<bool> isCurrent,
        object observationDomain = null, MessageApplyReceipt parent = null)
    {
        this.failed = failed; this.completed = completed; this.isCurrent = isCurrent;
        ObservationDomain = observationDomain; this.parent = parent;
    }
    internal bool Observes(object domain) => domain != null &&
        (ReferenceEquals(ObservationDomain, domain) || parent != null && parent.Observes(domain));
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
    internal static bool Observes(IMessageApplyObserver observer) => current != null && current.Observes(observer);
    internal static bool IsCurrent => current == null || current.IsCurrent;
    internal static MessageApplyScope Begin(IMessageApplyObserver observer, Type type) =>
        observer == null ? null : BeginReceipt(observer.Begin(type), observer);

    /// <summary>Composes source validation and descendant accounting; grants no execution permission.</summary>
    public static MessageApplyScope Observe(Func<bool> isCurrent, Action<string> failed, Action completed)
    {
        if (isCurrent == null) throw new ArgumentNullException(nameof(isCurrent));
        if (failed == null) throw new ArgumentNullException(nameof(failed));
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        return BeginReceipt(new MessageApplyReceipt(failed, completed, isCurrent), null);
    }

    private static MessageApplyScope BeginReceipt(MessageApplyReceipt value, object domain)
    {
        if (value == null) throw new ArgumentException("An observation receipt is required", nameof(value));
        var parent = current;
        parent?.Retain();
        var composed = new MessageApplyReceipt(reason =>
        {
            try { value.Fail(reason); }
            finally { parent?.Fail(reason); }
        }, () =>
        {
            try { value.Release(); }
            finally { parent?.Release(); }
        }, () => value.IsCurrent && (parent == null || parent.IsCurrent),
            value.ObservationDomain ?? domain, parent);
        return new MessageApplyScope(composed, true);
    }
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
        internal bool IsCurrent
        {
            get
            {
                try { return receipt == null || receipt.IsCurrent; }
                catch (Exception error)
                {
                    try { Complete(error.ToString()); }
                    catch (Exception completionError)
                    {
                        Serilog.Log.Error(completionError,
                            "Apply observation completion failed after source validation threw {ValidationError}", error);
                    }
                    throw;
                }
            }
        }
        internal IDisposable Activate() => receipt == null ? null : new MessageApplyScope(receipt, false);
        internal void Complete(string failure = null)
        {
            var value = Interlocked.Exchange(ref receipt, null);
            if (value == null) return;
            try { if (failure != null) value.Fail(failure); }
            finally { value.Release(); }
        }
    }
}
