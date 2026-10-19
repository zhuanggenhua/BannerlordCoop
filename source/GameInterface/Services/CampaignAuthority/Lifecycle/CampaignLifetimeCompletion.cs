using Autofac;
using Common;
using Common.Messaging;
using Common.Logging;
using GameInterface.Registry.Auto;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.CampaignAuthority.Lifecycle;

public interface ICampaignLifetimeCompletionDrain
{
    bool IsComplete { get; }
}

internal sealed class CampaignLifetimeCompletionModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder.RegisterType<CampaignLifetimeCompletion>()
        .AsSelf().As<ICampaignLifetimeCompletionDrain>().InstancePerLifetimeScope();
}

/// <summary>
/// Retains existing identities only through their evidenced native logical completion boundary.
/// It never manufactures/re-registers an identity or changes the generic destroy protocol.
/// Faulted scopes deliberately survive until the session container is disposed.
/// </summary>
internal sealed class CampaignLifetimeCompletion : ICampaignLifetimeCompletionDrain
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignLifetimeCompletion>();
    internal sealed class ArmyScope
    {
        internal Army Army;
        internal int Depth;
        internal bool Dispersed;
        internal bool Faulted;
        internal AuthorityLease Lease;
    }

    internal sealed class MapScope
    {
        internal MapEvent Event;
        internal HashSet<PartyBase> Participants;
        internal HashSet<PartyBase> Held = new();
        internal bool Faulted;
        internal AuthorityLease Lease;
        internal int Depth;
    }

    private readonly IObjectManager objects;
    private readonly IMessageBroker broker;
    private readonly ICampaignAuthorityContext authority;
    private readonly Dictionary<Army, ArmyScope> armies = new();
    private readonly List<MapScope> events = new();
    // Includes a publication failure after a scope has left the stack.
    private bool faulted;
    public bool IsComplete => !faulted && armies.Count == 0 && events.Count == 0;

    public CampaignLifetimeCompletion(IObjectManager objects, IMessageBroker broker, ICampaignAuthorityContext authority)
    {
        this.objects = objects;
        this.broker = broker;
        this.authority = authority;
    }

    private bool TryGetLease(out AuthorityLease lease) => authority.TryGetLocalAuthorityLease(out lease);
    private bool IsCurrent(AuthorityLease lease) => TryGetLease(out var current) && current == lease;

    internal ArmyScope EnterArmy(Army army)
    {
        if (army == null || !TryGetLease(out var lease) || !objects.TryGetHandle(army, out var handle)) return null;
        if (!armies.TryGetValue(army, out var scope))
        {
            scope = new ArmyScope { Army = army, Lease = lease };
            armies.Add(army, scope);
            Logger.Debug("[LifetimeCompletion] Army.begin handle={Handle} epoch={Epoch}", handle, lease.Epoch);
        }
        if (scope.Faulted || !IsCurrent(scope.Lease))
        {
            faulted = true;
            throw new InvalidOperationException("Army lifetime completion crossed a failed scope or authority lease");
        }
        scope.Depth++;
        return scope;
    }

    internal bool MayDisperse(ArmyScope scope)
    {
        if (scope == null || !scope.Dispersed) return true;
        if (scope.Army._armyIsDispersing || scope.Army.Parties.Count != 0 || scope.Army.Kingdom != null || !IsCurrent(scope.Lease))
        {
            scope.Faulted = faulted = true;
            throw new InvalidOperationException("Completed Army lifetime changed before its logical boundary");
        }
        return false;
    }

    internal void LeaveArmy(ArmyScope scope, bool wasDisperse, bool originalRan, Exception error)
    {
        if (scope == null) return;
        if (error != null || !IsCurrent(scope.Lease)) scope.Faulted = faulted = true;
        if (wasDisperse && originalRan && error == null)
        {
            // A native reentrant _armyIsDispersing early-return is not completion.
            if (!scope.Army._armyIsDispersing && scope.Army.Parties.Count == 0 && scope.Army.Kingdom == null)
                scope.Dispersed = true;
        }
        scope.Depth--;
        if (scope.Depth != 0 || scope.Faulted) return;
        if (scope.Dispersed)
        {
            MayDisperse(scope); // Revalidate after every ordinary outer postfix/subscriber.
            PublishDestroy(scope.Army);
        }
        armies.Remove(scope.Army);
        Logger.Debug("[LifetimeCompletion] Army.end dispersed={Dispersed} epoch={Epoch}", scope.Dispersed, scope.Lease.Epoch);
    }

    internal MapScope EnterFinalization(MapEvent mapEvent)
    {
        if (mapEvent == null || mapEvent.IsFinalized || !TryGetLease(out var lease)) return null;
        var existing = events.LastOrDefault(scope => ReferenceEquals(scope.Event, mapEvent));
        if (existing != null)
        {
            if (existing.Faulted || !IsCurrent(existing.Lease))
            {
                faulted = true;
                throw new InvalidOperationException("Reentrant finalization crossed a failed scope or authority lease");
            }
            existing.Depth++;
            return existing;
        }
        var scope = new MapScope
        {
            Event = mapEvent,
            Lease = lease,
            Depth = 1,
            Participants = new HashSet<PartyBase>(mapEvent.InvolvedParties.Where(party => party != null))
        };
        events.Add(scope);
        Logger.Debug("[LifetimeCompletion] MapEvent.begin id={MapEventId} epoch={Epoch}", mapEvent.StringId, lease.Epoch);
        return scope;
    }

    internal bool TryHoldGarrison(MobileParty party)
    {
        // Narrow positive match: existing NPC garrison identity in this exact active finalization graph.
        if (party == null || !party.IsGarrison || party.Party == null || !objects.TryGetHandle(party.Party, out _))
            return false;
        for (int index = events.Count - 1; index >= 0; index--)
        {
            var scope = events[index];
            if (!scope.Participants.Contains(party.Party)) continue;
            if (scope.Faulted || !IsCurrent(scope.Lease))
            {
                faulted = true;
                throw new InvalidOperationException("Garrison lifetime completion crossed a failed scope or authority lease");
            }
            scope.Held.Add(party.Party);
            Logger.Debug("[LifetimeCompletion] PartyBase.hold party={PartyId} event={MapEventId} epoch={Epoch}", party.StringId, scope.Event.StringId, scope.Lease.Epoch);
            return true;
        }
        return false;
    }

    internal void LeaveFinalization(MapScope scope, bool originalRan, Exception error)
    {
        if (scope == null) return;
        scope.Depth--;
        if (error != null || !originalRan || !scope.Event.IsFinalized || !IsCurrent(scope.Lease))
        {
            scope.Faulted = faulted = true;
            return;
        }
        if (scope.Depth != 0 || scope.Faulted) return;
        // Called by the Harmony finalizer, after the entire original and every ordinary postfix.
        // MapEventFinalized subscribers and the original MapEvent destroy have already run.
        foreach (var party in scope.Held) PublishDestroy(party);
        events.Remove(scope);
        Logger.Debug("[LifetimeCompletion] MapEvent.end released={HeldCount} epoch={Epoch}", scope.Held.Count, scope.Lease.Epoch);
    }

    private void PublishDestroy<T>(T instance) where T : class
    {
        try
        {
            // The original generic handler remains responsible for remove and original network destroy.
            broker.Publish(this, new InstanceDestroyed<T>(instance));
            if (objects.Contains(instance))
                throw new InvalidOperationException($"Original destroy did not retire completed {typeof(T).Name} identity");
        }
        catch
        {
            faulted = true;
            throw;
        }
    }
}
