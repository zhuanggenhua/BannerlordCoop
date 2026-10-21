using Common.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading;

namespace Common.Messaging;

public interface IMessageBroker : IDisposable
{
    void Publish<T>(object source, T message) where T : IMessage;

    void Subscribe<T>(Action<MessagePayload<T>> subscription) where T : IMessage;

    void Unsubscribe<T>(Action<MessagePayload<T>> subscription) where T : IMessage;
}

public class MessageBroker : IMessageBroker
{
    private static readonly ILogger Logger = LogManager.GetLogger<MessageBroker>();
    protected static MessageBroker instance;
    protected readonly Dictionary<Type, List<WeakDelegate>> subscribers;
    // Guards subscription dictionary/list access and the two lists below. They replace an Interlocked counter, and a counter cannot be left
    // unbalanced by an interleaving, while a lost list update would mark a list as walked for good
    // and stop pruning it for the lifetime of the broker
    private readonly object bookkeepingLock = new object();
    // Subscriber lists a publish is currently walking, one entry per running publish, so pruning a
    // list holds off while a loop is stepping through it by index
    private readonly List<List<WeakDelegate>> walkedByPublish = new List<List<WeakDelegate>>();
    // Lists whose pruning was skipped for that reason, caught up by the publish that leaves last
    private readonly List<List<WeakDelegate>> prunePending = new List<List<WeakDelegate>>();
    public static MessageBroker Instance { 
        get
        {
            instance ??= new MessageBroker();
            return instance;
        } 
    }

    private readonly object observerGate = new object();
    private readonly List<ObserverRegistration> applyObservers = new();
    private long observerRevision;

    private sealed class ObserverRegistration : IDisposable
    {
        internal readonly IMessageApplyObserver Observer;
        private Action release;
        internal ObserverRegistration(IMessageApplyObserver observer, Action release)
        { Observer = observer; this.release = release; }
        internal bool IsActive => Volatile.Read(ref release) != null;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    private IMessageApplyObserver[] Observers(out long revision)
    {
        lock (observerGate)
        {
            revision = observerRevision;
            return applyObservers.Where(value => value.IsActive).Select(value => value.Observer).Distinct().ToArray();
        }
    }
    private static bool IsConfirmed(IMessageApplyObserver observer) =>
        observer is not IMessageApplySessionObserver session || session.IsConfirmed;
    private static void RejectObservers(IEnumerable<IMessageApplyObserver> observers, string reason)
    {
        // Session/identity locks must never be acquired while holding observerGate.
        foreach (var observer in observers)
            if (observer is IMessageApplySessionObserver session) session.RejectBinding(reason);
    }

    /// <summary>Owned registration. Idle containers may coexist, never two confirmed sessions.</summary>
    public IDisposable ObserveApplies(IMessageApplyObserver observer, Action detached = null)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        while (true)
        {
            var existing = Observers(out var revision);
            if (IsConfirmed(observer) && existing.Any(value => !ReferenceEquals(value, observer) && IsConfirmed(value)))
            {
                const string reason = "A broker cannot observe two active campaign sessions.";
                RejectObservers(existing.Concat(new[] { observer }), reason);
                throw new InvalidOperationException(reason);
            }
            ObserverRegistration registration = null;
            registration = new ObserverRegistration(observer, () =>
            {
                lock (observerGate) { applyObservers.Remove(registration); observerRevision++; }
                detached?.Invoke();
            });
            lock (observerGate)
            {
                // Recheck registration changes without calling a session getter under this lock.
                if (revision != observerRevision) continue;
                applyObservers.Add(registration); observerRevision++;
                return registration;
            }
        }
    }

    private IMessageApplyObserver PublicationObserver()
    {
        var observers = Observers(out _);
        if (observers.Length <= 1) return observers.FirstOrDefault();
        const string reason = "Classified campaign result has ambiguous session bindings; no apply was executed.";
        RejectObservers(observers, reason);
        throw new InvalidOperationException(reason);
    }

    private IMessageApplyObserver ValidateClassifiedPublication()
    {
        try
        {
            var observer = PublicationObserver();
            if (!MessageApplyScope.IsCurrent)
                throw new OperationCanceledException("Classified publication outlived its session binding or source lease.");
            return observer;
        }
        catch (Exception ex)
        {
            // Fault the receipt's captured source lease even if its identity has already changed.
            MessageApplyScope.Fail(ex.ToString());
            throw;
        }
    }

    public MessageBroker()
    {
        subscribers = new Dictionary<Type, List<WeakDelegate>>();
    }

    public virtual void Publish<T>(object source, T message) where T : IMessage
    {
        if (message == null)
            return;

        bool classified = message is ICampaignAuthorityScopedMessage;
        // Even an explicit observation cannot make a process-global multi-session delivery safe.
        var observer = classified ? ValidateClassifiedPublication() : null;
        using var apply = classified && !MessageApplyScope.Observes(observer)
            ? MessageApplyScope.Begin(observer, typeof(T)) : null;
        List<WeakDelegate> delegates;
        lock (bookkeepingLock)
        {
            if (!subscribers.TryGetValue(typeof(T), out delegates) || delegates == null || delegates.Count == 0)
                delegates = null;
            else walkedByPublish.Add(delegates);
        }
        if (delegates == null)
        {
            if (classified) MessageApplyScope.Fail("No subscriber for classified campaign result.");
            return;
        }
        var payload = new MessagePayload<T>(source, message);
        try
        {
            for (int i = 0; ; i++)
            {
                WeakDelegate weakDelegate;
                bool dead;
                lock (bookkeepingLock)
                {
                    if (i >= delegates.Count) break;
                    weakDelegate = delegates[i];
                    dead = !weakDelegate.IsAlive;
                    if (dead) delegates.RemoveAt(i--);
                }
                if (dead)
                {
                    // Subscriptions are weak by design, but a collected target means this message is
                    // silently not handled — name it, because a lost handler is otherwise invisible
                    // (the classic trap: a closure/lambda subscription nothing kept alive).
                    Logger.Warning("Dropping dead subscriber {Method} for {MessageType}: its target was garbage collected",
                        weakDelegate.Method?.Name ?? "<unknown>", typeof(T).Name);
                    MessageApplyScope.Fail("Classified campaign result lost a collected subscriber.");
                    continue;
                }

                // A preceding subscriber may have changed the binding/lease or appended a foreign
                // subscriber. Keep ordinary live-list reentry, but never continue a stale critical apply.
                if (classified) ValidateClassifiedPublication();
                try
                {
                    // Making synchronous to maintain sequencing of packets
                    weakDelegate.Invoke(new object[] { payload });
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    MessageApplyScope.Fail(ex.InnerException.ToString());
                    // MethodInfo.Invoke wraps what the handler threw, and the wrapper's own frames end here
                    Logger.Error(ex.InnerException, "Failed to run {Method} for {MessageType}",
                        weakDelegate.Method?.Name ?? "<unknown>", typeof(T).Name);
                }
                catch (Exception ex)
                {
                    MessageApplyScope.Fail(ex.ToString());
                    Logger.Error(ex, "Failed to run {Method} for {MessageType}",
                        weakDelegate.Method?.Name ?? "<unknown>", typeof(T).Name);
                }
            }
        }
        finally
        {
            lock (bookkeepingLock)
            {
                walkedByPublish.Remove(delegates);
                if (!walkedByPublish.Contains(delegates) && prunePending.Remove(delegates))
                {
                    delegates.RemoveAll(weakDelegate => !weakDelegate.IsAlive);
                    if (delegates.Count == 0 && subscribers.TryGetValue(typeof(T), out var current) && current == delegates)
                        subscribers.Remove(typeof(T));
                }
            }
        }
    }

    public virtual void Subscribe<T>(Action<MessagePayload<T>> subscription) where T : IMessage
    {
        lock (bookkeepingLock)
        {
            var delegates = subscribers.TryGetValue(typeof(T), out var current) ? current : new List<WeakDelegate>();
            RemoveDeadSubscribers(delegates);
            if (!delegates.Contains(subscription)) delegates.Add(subscription);
            subscribers[typeof(T)] = delegates;
        }
    }

    public virtual void Unsubscribe<T>(Action<MessagePayload<T>> subscription) where T : IMessage
    {
        lock (bookkeepingLock)
        {
            if (!subscribers.TryGetValue(typeof(T), out var delegates)) return;
            if (delegates.Contains(new WeakDelegate(subscription))) delegates.Remove(subscription);
            RemoveDeadSubscribers(delegates);
            if (delegates.Count == 0) subscribers.Remove(typeof(T));
        }
    }

    // Entries whose target was collected otherwise sit here until this message type is published again.
    // Pruning a list that a publish is stepping through by index would move entries past its position,
    // so that list is noted here and pruned in the finally of that publish instead.
    private void RemoveDeadSubscribers(List<WeakDelegate> delegates)
    {
        lock (bookkeepingLock)
        {
            if (walkedByPublish.Contains(delegates))
            {
                if (!prunePending.Contains(delegates))
                    prunePending.Add(delegates);
                return;
            }
            delegates.RemoveAll(weakDelegate => !weakDelegate.IsAlive);
        }
    }

    public virtual void Dispose()
    {
        ObserverRegistration[] bindings;
        lock (observerGate) bindings = applyObservers.ToArray();
        foreach (var binding in bindings) binding.Dispose();
        lock (bookkeepingLock) subscribers?.Clear();
    }
}
