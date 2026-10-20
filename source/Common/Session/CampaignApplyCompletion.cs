using Common.Messaging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Common.Session;

/// <summary>Apply evidence owned by an existing session, never a second authority/registry manager.</summary>
public sealed class CampaignApplyCompletion : IMessageApplyObserver
{
    private sealed class State { internal int Pending; internal string Fault; }
    private static readonly ConditionalWeakTable<ISessionIdentity, CampaignApplyCompletion> sessions = new();
    private readonly ISessionIdentity identity;
    private readonly object gate = new object();
    private readonly Dictionary<AuthorityLease, State> states = new();
    private CampaignApplyCompletion(ISessionIdentity identity) { this.identity = identity; }
    public static CampaignApplyCompletion For(ISessionIdentity identity) =>
        sessions.GetValue(identity ?? throw new ArgumentNullException(nameof(identity)), value => new CampaignApplyCompletion(value));
    public static void Attach(IMessageBroker broker, ISessionIdentity identity)
    {
        if (identity != null && broker is MessageBroker concrete) concrete.ObserveApplies(For(identity));
    }
    public IDisposable Observe(Type messageType) => MessageApplyScope.Begin(this, messageType);
    public MessageApplyReceipt Begin(Type messageType)
    {
        bool confirmed = identity.TryGetConfirmedLease(out var lease);
        // Before the initial lease is installed, retain failures under the session sentinel.
        // Original join validation still owns those messages; observation grants no provenance.
        lock (gate)
        {
            if (!states.TryGetValue(lease, out var state)) states.Add(lease, state = new State());
            state.Pending++;
            return new MessageApplyReceipt(reason =>
            { lock (gate) state.Fault ??= messageType.FullName + ": " + reason; }, () =>
            { lock (gate) state.Pending--; }, () => !confirmed || identity.CurrentLease == lease);
        }
    }
    public void RecordFailure(AuthorityLease lease, string reason)
    {
        lock (gate)
        {
            if (!states.TryGetValue(lease, out var state)) states.Add(lease, state = new State());
            state.Fault ??= reason;
        }
    }
    /// <summary>A fault stays attached to its full source lease; later epochs cannot erase it.</summary>
    public bool CanAcknowledgeThrough(AuthorityLease boundary, out string reason)
    {
        if (!boundary.IsValid) { reason = "Invalid apply acknowledgement boundary."; return false; }
        lock (gate)
        {
            foreach (var pair in states)
            {
                if (pair.Key.IsValid && (pair.Key.RoomId != boundary.RoomId || pair.Key.Epoch > boundary.Epoch)) continue;
                if (pair.Value.Fault != null)
                { reason = "apply fault at epoch " + pair.Key.Epoch + ": " + pair.Value.Fault; return false; }
                if (pair.Value.Pending != 0)
                { reason = "pending applies at epoch " + pair.Key.Epoch + ": " + pair.Value.Pending; return false; }
            }
            reason = null; return true;
        }
    }
}
