using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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
    private readonly MessageApplyReceipt source;
    internal object ObservationDomain { get; }
    private int outstanding = 1;
    internal MessageApplyReceipt(Action<string> failed, Action completed, Func<bool> isCurrent,
        object observationDomain = null, MessageApplyReceipt parent = null, MessageApplyReceipt source = null)
    {
        this.failed = failed; this.completed = completed; this.isCurrent = isCurrent;
        ObservationDomain = observationDomain; this.parent = parent;
        this.source = source;
    }
    // Sources precede ambient parents. A retained activation may share either ancestor,
    // but a query or failure propagation must invoke each local callback only once.
    private IEnumerable<MessageApplyReceipt> Ancestors()
    {
        var visited = new HashSet<MessageApplyReceipt>();
        var pending = new Stack<(MessageApplyReceipt Receipt, bool Emit)>();
        pending.Push((this, false));
        while (pending.Count != 0)
        {
            var item = pending.Pop();
            if (item.Emit) { yield return item.Receipt; continue; }
            if (!visited.Add(item.Receipt)) continue;
            if (item.Receipt.parent != null) pending.Push((item.Receipt.parent, false));
            pending.Push((item.Receipt, true));
            if (item.Receipt.source != null) pending.Push((item.Receipt.source, false));
        }
    }
    internal bool Observes(object domain) => Observes(domain, out _);
    internal bool Observes(object domain, out int visitedCount)
    {
        visitedCount = 0;
        if (domain == null) return false;
        foreach (var ancestor in Ancestors())
        {
            visitedCount++;
            if (ReferenceEquals(ancestor.ObservationDomain, domain)) return true;
        }
        return false;
    }
    internal bool IsCurrent
    {
        get
        {
            foreach (var ancestor in Ancestors())
                if (ancestor.isCurrent != null && !ancestor.isCurrent()) return false;
            return true;
        }
    }
    internal void Retain() => Interlocked.Increment(ref outstanding);
    internal void Release() { if (Interlocked.Decrement(ref outstanding) == 0) completed(); }
    internal void Fail(string reason)
    {
        ExceptionDispatchInfo firstFailure = null;
        foreach (var ancestor in Ancestors())
        {
            try { ancestor.failed?.Invoke(reason); }
            catch (Exception error)
            {
                if (firstFailure == null) firstFailure = ExceptionDispatchInfo.Capture(error);
                else Serilog.Log.Error(error,
                    "Apply observation failure callback failed while reporting {ApplyFailureReason}", reason);
            }
        }
        firstFailure?.Throw();
    }
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

    private static MessageApplyScope BeginReceipt(MessageApplyReceipt value, object domain, Func<bool> sourceCurrent = null)
    {
        if (value == null) throw new ArgumentException("An observation receipt is required", nameof(value));
        var parent = current;
        parent?.Retain();
        var composed = new MessageApplyReceipt(null, () =>
        {
            try { value.Release(); }
            catch (Exception error)
            {
                ReportObservationFailure(reason => parent?.Fail(reason), error);
                throw;
            }
            finally { parent?.Release(); }
        }, sourceCurrent,
            value.ObservationDomain ?? domain, parent, value);
        return new MessageApplyScope(composed, true);
    }

    /// <summary>Retains an observed pending record; null means there was no observation to retain.</summary>
    public static RetainedObservation RetainCurrent() => current == null ? null : new RetainedObservation(current);

    public sealed class RetainedObservation : IDisposable
    {
        private readonly object gate = new object();
        private MessageApplyReceipt receipt;
        private string failure;

        internal RetainedObservation(MessageApplyReceipt receipt)
        {
            this.receipt = receipt;
            receipt.Retain();
        }

        private MessageApplyReceipt Acquire()
        {
            lock (gate)
            {
                var value = receipt;
                value?.Retain();
                return value;
            }
        }

        public bool IsCurrent
        {
            get
            {
                var value = Acquire();
                if (value == null) return false;
                bool valid;
                try
                {
                    valid = value.IsCurrent;
                    lock (gate) valid = valid && ReferenceEquals(receipt, value);
                }
                catch (Exception error)
                {
                    ReportObservationFailure(Complete, error);
                    ReleaseAfterValidationFailure(value, error);
                    throw;
                }
                value.Release();
                return valid;
            }
        }

        /// <summary>Activates on the calling thread; dispose on that same thread after applying the record.</summary>
        public IDisposable Activate()
        {
            var value = Acquire();
            if (value == null) throw new ObjectDisposedException(nameof(RetainedObservation));
            try
            {
                if (!value.IsCurrent) throw new OperationCanceledException("Retained apply source is no longer current.");
                lock (gate)
                {
                    if (!ReferenceEquals(receipt, value)) throw new ObjectDisposedException(nameof(RetainedObservation));
                }
                var parent = current;
                return new ActivationScope(BeginReceipt(value, null, () => Volatile.Read(ref failure) == null), this, parent);
            }
            catch (Exception error)
            {
                ReportObservationFailure(Complete, error);
                ReleaseAfterValidationFailure(value, error);
                throw;
            }
        }

        public void Complete(string failure = null)
        {
            MessageApplyReceipt value;
            lock (gate)
            {
                value = receipt;
                if (value == null) return;
                this.failure = failure;
                receipt = null;
            }
            try { if (failure != null) value.Fail(failure); }
            finally { value.Release(); }
        }

        public void Dispose() => Complete("Retained apply observation was disposed before completion.");

        private sealed class ActivationScope : IDisposable
        {
            private MessageApplyScope scope;
            private readonly RetainedObservation owner;
            private readonly MessageApplyReceipt parent;

            internal ActivationScope(MessageApplyScope scope, RetainedObservation owner, MessageApplyReceipt parent)
            { this.scope = scope; this.owner = owner; this.parent = parent; }

            public void Dispose()
            {
                var value = scope;
                if (value == null) return;
                scope = null;
                try
                {
                    var reason = Volatile.Read(ref owner.failure);
                    if (reason != null) parent?.Fail(reason);
                }
                finally { value.Dispose(); }
            }
        }
    }

    private static void ReportObservationFailure(Action<string> complete, Exception error)
    {
        try { complete(error.ToString()); }
        catch (Exception completionError)
        {
            Serilog.Log.Error(completionError,
                "Apply observation failure reporting failed after {ApplyError}", error);
        }
    }

    private static void ReleaseAfterValidationFailure(MessageApplyReceipt value, Exception error)
    {
        try { value.Release(); }
        catch (Exception completionError)
        {
            Serilog.Log.Error(completionError,
                "Apply observation release failed after source validation threw {ValidationError}", error);
        }
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
                    ReportObservationFailure(Complete, error);
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
