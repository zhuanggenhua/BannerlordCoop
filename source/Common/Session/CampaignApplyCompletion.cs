using Common.Messaging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Common.Session;

/// <summary>Apply evidence owned by an existing session, never a second authority/registry manager.</summary>
public sealed class CampaignApplyCompletion : IMessageApplySessionObserver
{
    private sealed class State { internal int Pending; internal string Fault; }
    private static readonly ConditionalWeakTable<ISessionIdentity, CampaignApplyCompletion> sessions = new();
    private readonly ISessionIdentity identity;
    private readonly object gate = new object();
    private readonly Dictionary<AuthorityLease, State> states = new();
    public event Action Progressed;
    private CampaignApplyCompletion(ISessionIdentity identity) { this.identity = identity; }
    public static CampaignApplyCompletion For(ISessionIdentity identity) =>
        sessions.GetValue(identity ?? throw new ArgumentNullException(nameof(identity)), value => new CampaignApplyCompletion(value));
    private int bindingCount;
    private long bindingGeneration;
    private bool wasBound;
    public bool IsConfirmed => identity.IsConfirmed;
    public void RejectBinding(string reason)
    {
        identity.TryGetConfirmedLease(out var lease);
        RecordFailure(lease, reason);
    }

    public sealed class Binding : IDisposable
    {
        private IDisposable registration;
        private readonly CampaignApplyCompletion owner;
        private readonly long generation;
        internal Binding(CampaignApplyCompletion owner, IDisposable registration, long generation)
        { this.owner = owner; this.registration = registration; this.generation = generation; }
        public bool IsActive => Volatile.Read(ref registration) != null && owner.IsBindingCurrent(generation);
        public IDisposable Observe(Type messageType)
        {
            if (!IsActive) throw new ObjectDisposedException(nameof(Binding));
            return owner.Observe(messageType);
        }
        public void Dispose() => Interlocked.Exchange(ref registration, null)?.Dispose();
    }
    public static Binding Attach(IMessageBroker broker, ISessionIdentity identity)
    {
        if (identity == null || broker is not MessageBroker concrete) return null;
        var owner = For(identity);
        long generation;
        lock (owner.gate)
        {
            owner.bindingCount++; owner.wasBound = true;
            generation = owner.bindingGeneration;
        }
        try { return new Binding(owner, concrete.ObserveApplies(owner, owner.ReleaseBinding), generation); }
        catch { owner.ReleaseBinding(); throw; }
    }
    private void ReleaseBinding()
    {
        lock (gate)
        {
            if (--bindingCount == 0) bindingGeneration++;
        }
    }
    private bool IsBindingCurrent(long generation)
    {
        lock (gate) return !wasBound || bindingCount > 0 && bindingGeneration == generation;
    }
    public IDisposable Observe(Type messageType) => MessageApplyScope.Begin(this, messageType);
    public IDisposable ObserveSource(Type messageType, AuthorityLease sourceLease)
    {
        if (!sourceLease.IsValid) throw new ArgumentException("A checkpoint apply requires a source lease.", nameof(sourceLease));
        if (!identity.TryGetConfirmedLease(out var current) || current != sourceLease)
        {
            RecordFailure(sourceLease, "Checkpoint apply source lease is not current and confirmed.");
            throw new InvalidOperationException("Checkpoint apply source lease is not current and confirmed.");
        }
        return MessageApplyScope.Begin(new SourceObserver(this, sourceLease), messageType);
    }
    private sealed class SourceObserver : IMessageApplyObserver
    {
        private readonly CampaignApplyCompletion owner; private readonly AuthorityLease lease;
        internal SourceObserver(CampaignApplyCompletion owner, AuthorityLease lease) { this.owner = owner; this.lease = lease; }
        public MessageApplyReceipt Begin(Type type) => owner.Begin(type, lease, () => owner.identity.CurrentLease == lease);
    }
    public MessageApplyReceipt Begin(Type messageType)
    {
        bool confirmed = identity.TryGetConfirmedLease(out var lease);
        // Before the initial lease is installed, retain failures under the session sentinel.
        // Original join validation still owns those messages; observation grants no provenance.
        return Begin(messageType, lease, () => confirmed ? identity.CurrentLease == lease : !identity.IsConfirmed);
    }
    private MessageApplyReceipt Begin(Type messageType, AuthorityLease lease, Func<bool> isCurrent)
    {
        lock (gate)
        {
            if (wasBound && bindingCount == 0)
                throw new ObjectDisposedException(nameof(Binding), "The campaign apply binding has ended.");
            var generation = bindingGeneration;
            if (!states.TryGetValue(lease, out var state)) states.Add(lease, state = new State());
            state.Pending++;
            return new MessageApplyReceipt(reason =>
            { lock (gate) state.Fault ??= messageType.FullName + ": " + reason; NotifyProgress(); }, () =>
            { lock (gate) state.Pending--; NotifyProgress(); }, () => IsBindingCurrent(generation) && isCurrent());
        }
    }
    public void RecordFailure(AuthorityLease lease, string reason)
    {
        lock (gate)
        {
            if (!states.TryGetValue(lease, out var state)) states.Add(lease, state = new State());
            state.Fault ??= reason;
        }
        NotifyProgress();
    }
    private void NotifyProgress()
    {
        var callbacks = Progressed;
        if (callbacks == null) return;
        foreach (Action callback in callbacks.GetInvocationList())
        {
            try { callback(); }
            catch (Exception ex)
            {
                // Observation must never change the original broker/queue exception behavior.
                var lease = identity.CurrentLease;
                lock (gate)
                {
                    if (!states.TryGetValue(lease, out var state)) states.Add(lease, state = new State());
                    state.Fault ??= "Apply continuation notification failed: " + ex;
                }
                Serilog.Log.Error(ex, "Critical apply continuation notification failed");
            }
        }
    }
    /// <summary>A fault stays attached to its full source lease; later epochs cannot erase it.</summary>
    public bool CanAcknowledgeThrough(AuthorityLease boundary, out string reason) => GetStatusThrough(boundary, out reason) == CampaignApplyStatus.Ready;
    public CampaignApplyStatus GetStatusThrough(AuthorityLease boundary, out string reason)
    {
        if (!boundary.IsValid) { reason = "Invalid apply acknowledgement boundary."; return CampaignApplyStatus.Faulted; }
        lock (gate)
        {
            int pending = 0;
            foreach (var pair in states)
            {
                if (pair.Key.IsValid && (pair.Key.RoomId != boundary.RoomId || pair.Key.Epoch > boundary.Epoch)) continue;
                if (pair.Value.Fault != null)
                { reason = "apply fault at epoch " + pair.Key.Epoch + ": " + pair.Value.Fault; return CampaignApplyStatus.Faulted; }
                pending += pair.Value.Pending;
            }
            reason = pending == 0 ? null : "pending classified applies: " + pending;
            return pending == 0 ? CampaignApplyStatus.Ready : CampaignApplyStatus.Pending;
        }
    }
}

public enum CampaignApplyStatus { Ready, Pending, Faulted }
