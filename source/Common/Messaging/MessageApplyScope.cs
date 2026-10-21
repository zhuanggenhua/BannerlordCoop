using System;
using System.Collections.Generic;
using Common.Logging;
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

// One finite failure, shared by its source and later ambient/cleanup propagation.
internal sealed class MessageApplyFailure
{
    private readonly object gate = new object();
    private readonly HashSet<MessageApplyReceipt> notified = new HashSet<MessageApplyReceipt>();
    private ExceptionDispatchInfo primary;
    private string reason;

    internal MessageApplyFailure(string reason = null, Exception original = null)
    {
        this.reason = reason;
        if (original != null) primary = ExceptionDispatchInfo.Capture(original);
    }
    internal string Reason => Volatile.Read(ref reason);
    internal bool HasFailure => Reason != null;
    internal bool TryNotify(MessageApplyReceipt receipt)
    { lock (gate) return notified.Add(receipt); }
    internal void Record(Exception error, string stage)
    {
        var captured = ExceptionDispatchInfo.Capture(error);
        var errorReason = Reason == null ? error.ToString() : null;
        bool secondary;
        lock (gate)
        {
            secondary = primary != null;
            if (!secondary) primary = captured;
            if (reason == null) reason = errorReason;
        }
        if (secondary) LogManager.GetLogger<MessageApplyReceipt>().Error(error,
            "Apply observation failure callback failed while reporting {ApplyFailureReason} during {FailureStage}", Reason, stage);
    }
    internal void ThrowPrimary()
    {
        ExceptionDispatchInfo error;
        lock (gate) error = primary;
        error?.Throw();
    }
}

/// <summary>One classified result and its synchronous/game-thread descendants.</summary>
public sealed class MessageApplyReceipt
{
    private readonly Action<string> failed;
    private readonly Action completed;
    private readonly Action<MessageApplyFailure> completedWithContext;
    private readonly Func<bool> isCurrent;
    private readonly MessageApplyReceipt parent;
    private readonly MessageApplyReceipt source;
    private readonly Func<MessageApplyFailure> inheritedFailure;
    internal object ObservationDomain { get; }
    private int outstanding = 1;
    internal MessageApplyReceipt(Action<string> failed, Action completed, Func<bool> isCurrent,
        object observationDomain = null, MessageApplyReceipt parent = null, MessageApplyReceipt source = null,
        Action<MessageApplyFailure> completedWithContext = null, Func<MessageApplyFailure> inheritedFailure = null)
    {
        this.failed = failed; this.completed = completed; this.isCurrent = isCurrent;
        ObservationDomain = observationDomain; this.parent = parent;
        this.source = source;
        this.completedWithContext = completedWithContext;
        this.inheritedFailure = inheritedFailure;
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
    internal void Release(MessageApplyFailure failure = null)
    {
        if (Interlocked.Decrement(ref outstanding) != 0) return;
        var context = failure ?? new MessageApplyFailure();
        try
        {
            if (completedWithContext != null) completedWithContext(context);
            else completed?.Invoke();
        }
        catch (Exception error) { context.Record(error, "completion release"); }
        if (failure == null) context.ThrowPrimary();
    }
    internal void Fail(string reason)
    {
        var failure = new MessageApplyFailure(reason);
        Fail(failure);
        failure.ThrowPrimary();
    }
    internal void Fail(MessageApplyFailure failure)
    {
        foreach (var ancestor in Ancestors())
        {
            if (!failure.TryNotify(ancestor)) continue;
            try { ancestor.failed?.Invoke(failure.Reason); }
            catch (Exception error)
            {
                failure.Record(error, "failure callback");
            }
        }
    }
    internal List<MessageApplyFailure> InheritedFailures()
    {
        var result = new List<MessageApplyFailure>();
        var seen = new HashSet<MessageApplyFailure>();
        foreach (var ancestor in Ancestors())
        {
            var failure = ancestor.inheritedFailure?.Invoke();
            if (failure != null && seen.Add(failure)) result.Add(failure);
        }
        return result;
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

    private static MessageApplyScope BeginReceipt(MessageApplyReceipt value, object domain, Func<bool> sourceCurrent = null,
        Func<MessageApplyFailure> inheritedFailure = null)
    {
        if (value == null) throw new ArgumentException("An observation receipt is required", nameof(value));
        var parent = current;
        parent?.Retain();
        var composed = new MessageApplyReceipt(null, null, sourceCurrent,
            value.ObservationDomain ?? domain, parent, value, failure =>
        {
            value.Release(failure);
            if (failure.HasFailure) parent?.Fail(failure);
            parent?.Release(failure);
        }, inheritedFailure);
        return new MessageApplyScope(composed, true);
    }

    /// <summary>Retains an observed pending record; null means there was no observation to retain.</summary>
    public static RetainedObservation RetainCurrent() => current == null ? null : new RetainedObservation(current);

    public sealed class RetainedObservation : IDisposable
    {
        private readonly object gate = new object();
        private MessageApplyReceipt receipt;
        private MessageApplyFailure failure;

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
                    CompleteValidationFailure(value, error);
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
            bool closedDuringValidation = false;
            try
            {
                if (!value.IsCurrent) throw new OperationCanceledException("Retained apply source is no longer current.");
                lock (gate)
                {
                    if (!ReferenceEquals(receipt, value))
                    {
                        closedDuringValidation = true;
                        throw new ObjectDisposedException(nameof(RetainedObservation));
                    }
                }
                var parent = current;
                return new ActivationScope(BeginReceipt(value, null, () => Volatile.Read(ref failure) == null,
                    () => Volatile.Read(ref failure)), this, parent);
            }
            catch (Exception error)
            {
                if (closedDuringValidation)
                {
                    // A successful owner close is not a source failure. Cleanup still preserves
                    // this original ownership error if a completion callback also fails.
                    var cleanup = new MessageApplyFailure(null, error);
                    value.Release(cleanup);
                    cleanup.ThrowPrimary();
                }
                else CompleteValidationFailure(value, error);
                throw;
            }
        }

        public void Complete(string failure = null)
        {
            MessageApplyReceipt value;
            var context = new MessageApplyFailure(failure);
            lock (gate)
            {
                value = receipt;
                if (value == null) return;
                this.failure = failure == null ? null : context;
                receipt = null;
            }
            if (failure != null) value.Fail(context);
            value.Release(context);
            context.ThrowPrimary();
        }

        private void CompleteValidationFailure(MessageApplyReceipt acquired, Exception error)
        {
            var context = new MessageApplyFailure(error.ToString(), error);
            MessageApplyReceipt owned;
            lock (gate)
            {
                owned = receipt;
                if (owned != null)
                {
                    failure = context;
                    receipt = null;
                }
            }
            acquired.Fail(context);
            owned?.Release(context);
            acquired.Release(context);
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
                var value = Interlocked.Exchange(ref scope, null);
                if (value == null) return;
                var failure = Volatile.Read(ref owner.failure);
                if (failure == null) value.Dispose();
                else
                {
                    parent?.Fail(failure);
                    value.Dispose(failure);
                    failure.ThrowPrimary();
                }
            }
        }
    }

    internal static IDisposable Suspend() => new MessageApplyScope(null, false);
    internal static Continuation Capture() => current == null ? null : new Continuation(current);
    public static void Fail(string reason) => current?.Fail(reason);
    public void Dispose() => Dispose(null);
    private void Dispose(MessageApplyFailure failure)
    {
        if (disposed) return;
        disposed = true;
        var value = receipt;
        receipt = null; current = previous;
        if (release) value?.Release(failure);
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
                    CompleteCore(error.ToString(), error, false);
                    throw;
                }
            }
        }
        internal IDisposable Activate() => receipt == null ? null : new MessageApplyScope(receipt, false);
        internal void Complete(string failure = null, Exception original = null) => CompleteCore(failure, original, true);
        private void CompleteCore(string failure, Exception original, bool throwPrimary)
        {
            var value = Interlocked.Exchange(ref receipt, null);
            if (value == null) return;
            var inherited = value.InheritedFailures();
            MessageApplyFailure context = null;
            if (original != null || inherited.Count == 0)
                context = new MessageApplyFailure(original?.ToString() ?? failure, original);
            foreach (var previous in inherited) value.Fail(previous);
            if (context?.HasFailure == true) value.Fail(context);
            var cleanup = context ?? inherited[0];
            value.Release(cleanup);
            if (!throwPrimary) return;
            context?.ThrowPrimary();
            foreach (var previous in inherited) previous.ThrowPrimary();
        }
    }
}
