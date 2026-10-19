using Common;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.ObjectReadiness;

public interface ICampaignObjectGraphReadiness
{
    void BeginReplicaCreation(MobileParty party);
    void PublishReplicaWhenReady(MobileParty party, Action publish);
    void ReferencesApplied();
    void CancelReplicaPublication(MobileParty party);
    void CompleteReplicaRemoval(MobileParty party);
    bool TryEnsureReady(out string reason);
}

internal sealed class CampaignObjectGraphReadiness : ICampaignObjectGraphReadiness, IDisposable
{
    private sealed class Creation
    {
        internal Action Publish;
        internal bool Published;
        internal bool Faulted;
        internal bool Removing;
        internal bool RemovalCompleted;
    }

    private readonly IObjectManager objects;
    private readonly ILogger logger;
    private ConditionalWeakTable<MobileParty, Creation> creations = new();
    private readonly Dictionary<MobileParty, Creation> pending = new();
    private bool checking;
    private Campaign campaign;
    private bool campaignCaptured;
    private volatile bool disposed;

    public CampaignObjectGraphReadiness(IObjectManager objects, ILogger logger)
    {
        this.objects = objects;
        this.logger = logger;
    }

    public void BeginReplicaCreation(MobileParty party)
    {
        RequireActiveGameThread();
        SynchronizeCampaign();
        if (party == null) throw new ArgumentNullException(nameof(party));
        if (creations.TryGetValue(party, out _)) return;
        var creation = new Creation();
        creations.Add(party, creation);
        pending.Add(party, creation);
        logger.Debug("[ObjectGraph] Pending replica creation {Party}", party.StringId);
    }

    public void PublishReplicaWhenReady(MobileParty party, Action publish)
    {
        RequireActiveGameThread();
        SynchronizeCampaign();
        if (publish == null) throw new ArgumentNullException(nameof(publish));
        if (party == null || !creations.TryGetValue(party, out var creation))
            throw new InvalidOperationException("Replica creation must be tracked before world publication.");
        if (creation.Published || creation.Faulted || creation.Removing || creation.Publish != null) return;
        creation.Publish = publish;
        ReferencesApplied();
    }

    public void ReferencesApplied()
    {
        RequireActiveGameThread();
        SynchronizeCampaign();
        if (checking || pending.Count == 0) return;
        checking = true;
        try
        {
            foreach (var entry in pending.ToArray())
            {
                if (disposed) break;
                if (!ReferenceEquals(campaign, Campaign.Current))
                {
                    SynchronizeCampaign();
                    break;
                }
                var creation = entry.Value;
                if (creation.RemovalCompleted && !objects.Contains(entry.Key))
                {
                    pending.Remove(entry.Key);
                    creations.Remove(entry.Key);
                    continue;
                }
                if (creation.Removing) continue;
                if (creation.Faulted || creation.Publish == null || !HasRegisteredGraph(entry.Key)) continue;
                try
                {
                    creation.Publish();
                    if (!ReferenceEquals(campaign, Campaign.Current))
                    {
                        SynchronizeCampaign();
                        break;
                    }
                    creation.Published = true;
                    creation.Publish = null;
                    if (!creation.Removing) pending.Remove(entry.Key);
                    logger.Debug("[ObjectGraph] Committed replica creation {Party}", entry.Key.StringId);
                }
                catch (Exception exception)
                {
                    creation.Faulted = true;
                    logger.Error(exception, "[ObjectGraph] World publication failed for {Party}; handoff is refused", entry.Key.StringId);
                }
            }
        }
        finally { checking = false; }
    }

    public void CancelReplicaPublication(MobileParty party)
    {
        RequireActiveGameThread();
        SynchronizeCampaign();
        if (party == null || !creations.TryGetValue(party, out var creation)) return;
        creation.Publish = null;
        creation.Removing = true;
        pending[party] = creation;
    }

    public void CompleteReplicaRemoval(MobileParty party)
    {
        RequireActiveGameThread();
        CancelReplicaPublication(party);
        if (party != null && creations.TryGetValue(party, out var creation)) creation.RemovalCompleted = true;
    }

    public bool TryEnsureReady(out string reason)
    {
        RequireActiveGameThread();
        ReferencesApplied();
        if (disposed)
        {
            reason = "session-disposed";
            return false;
        }
        reason = pending.Count == 0 ? null : string.Join(",", pending.Select(entry =>
            entry.Key.StringId + (entry.Value.Faulted ? ":publication-fault" :
                entry.Value.Removing ? ":removal-pending" : ":creation-pending")));
        return pending.Count == 0;
    }

    private bool HasRegisteredGraph(MobileParty party)
    {
        var owner = party.Party;
        return HasHandle(party) && owner != null && HasHandle(owner) &&
            ReferenceEquals(owner.MobileParty, party) && HasHandle(owner.ItemRoster) &&
            owner.MemberRoster != null && owner.PrisonRoster != null &&
            !ReferenceEquals(owner.MemberRoster, owner.PrisonRoster) &&
            HasHandle(owner.MemberRoster) && HasHandle(owner.PrisonRoster) &&
            ReferenceEquals(owner.MemberRoster.OwnerParty, owner) &&
            ReferenceEquals(owner.PrisonRoster.OwnerParty, owner);
    }

    private bool HasHandle(object value) => value != null && objects.TryGetHandle(value, out var handle) && handle != 0;

    private void SynchronizeCampaign()
    {
        var current = Campaign.Current;
        if (campaignCaptured && ReferenceEquals(current, campaign)) return;
        if (campaignCaptured && pending.Count != 0)
            logger.Warning("[ObjectGraph] Discarding {Count} construction callbacks from the previous Campaign instance", pending.Count);
        pending.Clear();
        creations = new ConditionalWeakTable<MobileParty, Creation>();
        campaign = current;
        campaignCaptured = true;
    }

    public void Dispose() => disposed = true;

    private void RequireActiveGameThread()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CampaignObjectGraphReadiness));
        if (!GameThread.Instance.IsGameThread)
            throw new InvalidOperationException("Object graph readiness must be checked on the game thread.");
    }
}
