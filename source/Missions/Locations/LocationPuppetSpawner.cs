using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Locations;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using Missions.Messages;
using SandBox;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Locations;

/// <summary>
/// Peer-side spawn application for a settlement location mission: spawns the NPCs the host replicates
/// over the mesh (<see cref="NetworkSpawnLocationAgents"/>) as local puppets driven by the host's
/// movement (SR-020..SR-025). Human puppets are built from a LOCAL roster entry's origin
/// (<see cref="ILocationPuppetRosterBinder"/>, SR-022) so native bookkeeping recognizes them; animals
/// re-spawn via <c>Mission.SpawnMonster</c> from their item identities. Records that arrive before
/// the mission or a render slot exists are buffered and drained on tick. Mirrors the battle
/// <c>PuppetSpawner</c>.
/// </summary>
public interface ILocationPuppetSpawner : IDisposable
{
    /// <summary>[Game thread] Retry buffered puppets (mission loading / render budget).</summary>
    void DrainPendingPuppets();

    /// <summary>
    /// The local mission finished setting up (player agent + teams exist). Until then every incoming
    /// record is buffered — <c>Mission.Current</c> is non-null while the scene is still LOADING, and
    /// spawning into a half-initialized mission corrupts team setup (the same reason join info is
    /// buffered).
    /// </summary>
    void NotifyMissionReady();

    /// <summary>True while any NPC spawn record is still buffered. A promoted host uses this together
    /// with its adopted bindings to tell "crowd exists, adopt it" from "nothing ever spawned,
    /// populate" (SR-014).</summary>
    bool HasPendingNpcRecords { get; }
}

/// <inheritdoc cref="ILocationPuppetSpawner"/>
public class LocationPuppetSpawner : ILocationPuppetSpawner
{
    private static readonly ILogger Logger = LogManager.GetLogger<LocationPuppetSpawner>();
    private const int MaxBufferedSpawnsPerTick = 64;

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ICoopMissionComponent coopMissionComponent;
    private readonly ILocationSession session;
    private readonly ILocationConversationAgentGuard conversationAgentGuard;
    private readonly ILocationAgentBindingMap bindingMap;
    private readonly ILocationPartyAgentMap partyAgentMap;
    private readonly ILocationPuppetRosterBinder rosterBinder;
    private readonly IBattleAgentBudget agentBudget;
    private readonly ILocationAgentSpawnBatchCodec spawnBatchCodec;
    private readonly ILocationAuthorityMigrator authorityMigrator;
    private readonly ILocationControllerWithdrawalState withdrawalState;

    private readonly object pendingPuppetLock = new object();
    private readonly List<PendingSpawn> pendingPuppets = new List<PendingSpawn>();
    private volatile bool disposed;

    private sealed class PendingSpawn
    {
        internal readonly LocationAgentSpawnData Data;
        internal readonly MessageApplyScope.RetainedObservation Observation;
        internal PendingSpawn(LocationAgentSpawnData data)
        { Data = data; Observation = MessageApplyScope.RetainCurrent(); }
    }

    // Spawning needs the local mission fully set up; Mission.Current alone is not that signal.
    private volatile bool missionReady;

    // Despawned ids (SR-026): a despawn can cross a still-buffered spawn record (or a catch-up
    // replay), so a tombstoned id must never (re)spawn. Per-mission lifetime bounds the set.
    private readonly object tombstoneLock = new object();
    private readonly HashSet<Guid> despawnedAgentIds = new HashSet<Guid>();

    // A point-use that arrived before its puppet spawned (records and point messages share the
    // reliable stream, but the puppet may still be buffered on budget/readiness) — consumed at
    // spawn. Game thread only.
    private readonly Dictionary<Guid, int> pendingPointUses = new Dictionary<Guid, int>();

    public LocationPuppetSpawner(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        ICoopMissionComponent coopMissionComponent,
        ILocationSession session,
        ILocationConversationAgentGuard conversationAgentGuard,
        ILocationAgentBindingMap bindingMap,
        ILocationPartyAgentMap partyAgentMap,
        ILocationPuppetRosterBinder rosterBinder,
        IBattleAgentBudget agentBudget,
        ILocationAgentSpawnBatchCodec spawnBatchCodec,
        ILocationAuthorityMigrator authorityMigrator,
        ILocationControllerWithdrawalState withdrawalState)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.coopMissionComponent = coopMissionComponent;
        this.session = session;
        this.conversationAgentGuard = conversationAgentGuard;
        this.bindingMap = bindingMap;
        this.partyAgentMap = partyAgentMap;
        this.rosterBinder = rosterBinder;
        this.agentBudget = agentBudget;
        this.spawnBatchCodec = spawnBatchCodec;
        this.authorityMigrator = authorityMigrator;
        this.withdrawalState = withdrawalState;

        messageBroker.Subscribe<NetworkSpawnLocationAgents>(Handle_NetworkSpawnLocationAgents);
        messageBroker.Subscribe<NetworkDespawnLocationAgents>(Handle_NetworkDespawnLocationAgents);
        messageBroker.Subscribe<NetworkNpcPointUse>(Handle_NetworkNpcPointUse);
        messageBroker.Subscribe<NetworkMissionPeerEntered>(Handle_PeerEntered);
        messageBroker.Subscribe<MissionPeerLeft>(Handle_PeerLeft);
        messageBroker.Subscribe<MissionPeerDisconnected>(Handle_PeerDisconnected);
    }

    public void Dispose()
    {
        disposed = true;
        messageBroker.Unsubscribe<NetworkSpawnLocationAgents>(Handle_NetworkSpawnLocationAgents);
        messageBroker.Unsubscribe<NetworkDespawnLocationAgents>(Handle_NetworkDespawnLocationAgents);
        messageBroker.Unsubscribe<NetworkNpcPointUse>(Handle_NetworkNpcPointUse);
        messageBroker.Unsubscribe<NetworkMissionPeerEntered>(Handle_PeerEntered);
        messageBroker.Unsubscribe<MissionPeerLeft>(Handle_PeerLeft);
        messageBroker.Unsubscribe<MissionPeerDisconnected>(Handle_PeerDisconnected);
        CancelPending(_ => true, "Location puppet spawner disposed before pending apply.");
    }

    // [Peer] The host's native systems removed NPCs (passage exit, churn, death): fade our puppets
    // out and tombstone the ids so a still-buffered spawn record cannot resurrect them (SR-026).
    private void Handle_NetworkDespawnLocationAgents(MessagePayload<NetworkDespawnLocationAgents> payload)
    {
        var ids = payload.What.AgentIds;
        if (ids == null || ids.Length == 0) return;

        lock (tombstoneLock)
        {
            foreach (var id in ids)
                despawnedAgentIds.Add(id);
        }
        CancelPending(data => System.Array.IndexOf(ids, data.AgentId) >= 0,
            "Location puppet despawned before pending apply.");

        var destinations = payload.What.DestinationLocationIds;

        GameThread.RunSafe(() =>
        {
            var registry = coopMissionComponent.AgentRegistry;
            int removed = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                if (!registry.TryGetAgentInfo(id, out var info)) continue;

                var agent = info.Agent;
                conversationAgentGuard.EndConversationWithAgent(agent);

                // SR-026: a passage exit moved the entry between location rosters on the host —
                // mirror it here (before the binding is forgotten) or a later promoted host holds
                // stale passage rosters and this NPC can never be selected to wander back.
                var destinationLocationId = destinations != null && i < destinations.Length
                    ? destinations[i]
                    : null;
                if (!string.IsNullOrEmpty(destinationLocationId)
                    && bindingMap.TryGet(id, out var binding)
                    && binding.RosterEntry != null)
                {
                    rosterBinder.TryMoveBoundEntry(agent, binding.RosterEntry.LocationId, destinationLocationId);
                }

                if (agent != null && agent.IsActive() && agent.Health > 0)
                {
                    bool hideMount = agent.HasMount && agent.MountAgent != null && agent.MountAgent.IsActive();
                    agent.FadeOut(false, hideMount);
                }

                coopMissionComponent.AgentMovementHandler.Interpolator.Forget(agent);
                registry.RemoveAgent(id);
                bindingMap.Forget(id);
                pendingPointUses.Remove(id);
                removed++;
            }

            if (removed > 0)
                Logger.Information("[LocationSync] Despawned {Count} NPC puppet(s) on the host's broadcast", removed);
        }, context: nameof(Handle_NetworkDespawnLocationAgents));
    }

    private void Handle_NetworkSpawnLocationAgents(MessagePayload<NetworkSpawnLocationAgents> payload)
    {
        NetworkSpawnLocationAgents message = payload.What;
        if (!spawnBatchCodec.TryDecode(message, out LocationAgentSpawnData[] agents))
        {
            Logger.Error(
                "[LocationTraffic] Rejected malformed spawn batch {TransferId} {BatchIndex}/{BatchCount} " +
                "with {RecordCount} declared record(s)",
                message.TransferId,
                message.BatchIndex + 1,
                message.BatchCount,
                message.RecordCount);
            return;
        }

        // One bounded game-thread action per wire batch preserves ReliableOrdered barriers without
        // adding one queue entry per agent. Non-blocking: this runs on the network thread while the
        // mission may still be loading.
        GameThread.RunSafe(
            () => SpawnPuppetBatch(message, agents),
            context: nameof(Handle_NetworkSpawnLocationAgents));
    }

    private void SpawnPuppetBatch(
        NetworkSpawnLocationAgents message,
        LocationAgentSpawnData[] agents)
    {
        if (disposed)
        {
            MessageApplyScope.Fail("Location puppet batch reached a disposed spawner.");
            return;
        }
        if (Mission.Current == null)
        {
            // The mission can still be LOADING on a joiner when its catch-up burst lands — buffer, don't drop.
            foreach (LocationAgentSpawnData data in agents)
            {
                if (data != null && data.AgentId != Guid.Empty)
                {
                    withdrawalState.IsRetainedFormerHostRecord(data.OwnerControllerId, data.AgentId);
                    QueuePending(new PendingSpawn(data));
                }
            }
            return;
        }

        int slotsAvailable = agentBudget.RemainingCapacity(agentBudget.CountLiveAgents(Mission.Current));
        foreach (LocationAgentSpawnData data in agents)
        {
            if (data == null || data.AgentId == Guid.Empty) continue;

            var pending = new PendingSpawn(data);
            try
            {
                if (!TryApplyPending(pending, ref slotsAvailable)) QueuePending(pending);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[LocationSync] Failed to spawn puppet {AgentId}; dropping it", data.AgentId);
            }
        }
    }

    private void QueuePending(PendingSpawn pending)
    {
        lock (pendingPuppetLock)
        {
            if (!disposed) { pendingPuppets.Add(pending); return; }
        }
        pending.Observation?.Complete("Location puppet spawner disposed before enqueue.");
    }

    private bool TryApplyPending(PendingSpawn pending, ref int slotsAvailable)
    {
        IDisposable active = null;
        Exception failure = null;
        try
        {
            if (disposed)
            {
                pending.Observation?.Complete("Location puppet spawner disposed before pending apply.");
                return true;
            }
            if (pending.Observation != null && !pending.Observation.IsCurrent)
            {
                pending.Observation.Complete("Location puppet pending source is no longer current.");
                return true;
            }
            active = pending.Observation?.Activate();
            // Source callbacks may reenter Dispose while this record is outside the queue.
            if (disposed)
            {
                pending.Observation?.Complete("Location puppet spawner disposed during source validation.");
                return true;
            }
            if (!TrySpawnPuppetNow(pending.Data, ref slotsAvailable, out var rejected)) return false;
            pending.Observation?.Complete(rejected);
            return true;
        }
        catch (Exception error)
        {
            failure = error;
            try { pending.Observation?.Complete(error.ToString()); }
            catch (Exception secondary)
            { Logger.Error(secondary, "Location pending apply failure reporting failed after {ApplyError}", error); }
            throw;
        }
        finally
        {
            if (failure == null) active?.Dispose();
            else
            {
                try { active?.Dispose(); }
                catch (Exception secondary)
                { Logger.Error(secondary, "Location pending apply scope release failed after {ApplyError}", failure); }
            }
        }
    }

    private void CancelPending(Predicate<LocationAgentSpawnData> matches, string reason)
    {
        var cancelled = new List<PendingSpawn>();
        lock (pendingPuppetLock)
        {
            pendingPuppets.RemoveAll(pending =>
            {
                if (!matches(pending.Data)) return false;
                cancelled.Add(pending);
                return true;
            });
        }
        CompleteCancelled(cancelled, reason);
    }

    private static void CompleteCancelled(IReadOnlyList<PendingSpawn> cancelled, string reason)
    {
        ExceptionDispatchInfo failure = null;
        foreach (var pending in cancelled)
        {
            try { pending.Observation?.Complete(reason); }
            catch (Exception error)
            {
                if (failure == null) failure = ExceptionDispatchInfo.Capture(error);
                else Logger.Error(error, "Additional location pending cancellation callback failed for {CancelReason}", reason);
            }
        }
        failure?.Throw();
    }

    // [Peer] The host's NPC started or stopped using a scene point: have our puppet use the SAME
    // local point — it then drives alignment, animation and occupancy natively, replacing the whole
    // replicate-the-outputs pin apparatus (floating/spinning/sliding sitters).
    private void Handle_NetworkNpcPointUse(MessagePayload<NetworkNpcPointUse> payload)
    {
        var message = payload.What;
        if (message.AgentId == Guid.Empty) return;

        GameThread.RunSafe(() =>
        {
            if (Mission.Current == null) return;

            if (!coopMissionComponent.AgentRegistry.TryGetAgentInfo(message.AgentId, out var info)
                || info.Agent == null)
            {
                // The puppet is still buffered (budget/readiness) — remember the point for its spawn.
                if (message.InUse) pendingPointUses[message.AgentId] = message.PointId;
                else pendingPointUses.Remove(message.AgentId);
                return;
            }

            if (message.InUse) ApplyPointUse(info.Agent, message.PointId, message.AgentId, resetToPointFrame: false);
            else StopPointUse(info.Agent);
        }, context: nameof(Handle_NetworkNpcPointUse));
    }

    // [Game thread] Resolve the scene point by its deterministic MissionObjectId and use it.
    private void ApplyPointUse(Agent agent, int pointId, Guid agentId, bool resetToPointFrame)
    {
        if (agent == null || !agent.IsActive()) return;

        UsableMissionObject point = null;
        foreach (var missionObject in Mission.Current.MissionObjects)
        {
            if (missionObject.Id.CreatedAtRuntime || missionObject.Id.Id != pointId) continue;
            point = missionObject as UsableMissionObject;
            break;
        }

        if (point == null)
        {
            Logger.Warning("[LocationSync] Point {PointId} for NPC {AgentId} did not resolve locally — puppet stays un-animated", pointId, agentId);
            return;
        }

        if (ReferenceEquals(agent.CurrentlyUsedGameObject, point)) return;

        if (point is SandBox.Objects.AnimationPoints.AnimationPoint)
        {
            // Every NEW authoritative performance starts from the local point's canonical
            // pre-arrival frame. Position reconciliation stops as soon as the point owns the
            // puppet, so a live transition cannot depend on later snapshots to finish alignment.
            LocationPointUseLifecycle.RestartFromCanonicalFrame(agent, point);
            Logger.Debug("[LocationSync] NPC {AgentId} restarted local point {PointId} arrival", agentId, pointId);
            return;
        }

        // Preserve the old generic StandingPoint/UsableMissionObject path: unlike AnimationPoint it
        // does not replace channel 0 with an arrival action. Catch-up still plants a fresh puppet at
        // its point, while a live transition retains the host-replicated action and movement.
        if (agent.CurrentlyUsedGameObject != null)
            agent.StopUsingGameObject(isSuccessful: true);

        var frame = point.GetUserFrameForAgent(agent);
        if (resetToPointFrame)
        {
            var direction = frame.Rotation.f.AsVec2.Normalized();
            agent.TeleportToPosition(frame.Origin.GetGroundVec3());
            agent.SetMovementDirection(in direction);
        }

        agent.UseGameObject(point);
        agent.SetTargetPositionAndDirection(frame.Origin.AsVec2, in frame.Rotation.f);
    }

    private static void StopPointUse(Agent agent)
    {
        if (agent == null || !agent.IsActive()) return;
        if (agent.CurrentlyUsedGameObject != null)
            agent.StopUsingGameObject(isSuccessful: true);
    }

    // [Game thread] Spawn one puppet, consuming render slots on success. Returns false to buffer.
    private bool TrySpawnPuppetNow(LocationAgentSpawnData data, ref int slotsAvailable, out string rejected)
    {
        rejected = null;
        var registry = coopMissionComponent.AgentRegistry;
        bool ownerIsRetainedFormerHost = withdrawalState.IsRetainedFormerHostRecord(
            data.OwnerControllerId,
            data.AgentId);

        bool ownerIsWithdrawnHost;
        if (IsWithdrawn(data.OwnerControllerId, out ownerIsWithdrawnHost) && !ownerIsWithdrawnHost)
        {
            rejected = "Location puppet owner withdrew before apply.";
            return true;                                                // stale player record — drop
        }

        // Mission.Current exists while the scene is still LOADING — gate on the same readiness the
        // join-info path uses, or the crowd spawns before teams/the player agent are initialized.
        if (!missionReady || Mission.Current == null) return false;     // still loading — buffer

        // A departed plain player's records are stale (its puppet despawns, nothing adopts it). A
        // departed HOST's records are the crowd itself: retained, spawned, and — when WE are the
        // promoted host — adopted below, so a partially-applied catch-up never loses NPCs (SR-014).
        if (IsTombstoned(data.AgentId))
        { rejected = "Location puppet was tombstoned before apply."; return true; }
        if (partyAgentMap.Contains(data.AgentId))
        { rejected = "Location puppet identity is owned by party join info."; return true; }
        if (registry.TryGetAgentInfo(data.AgentId, out _)) return true; // already spawned (incl. our own natives) — dedupe

        int slotsNeeded = 1;
        if (data.Kind == LocationAgentKind.Human && data.SpawnEquipment != null)
            slotsNeeded = agentBudget.SlotsForEquipment(data.SpawnEquipment);
        if (slotsNeeded > slotsAvailable) return false;                 // at capacity — buffer

        LocationCharacter rosterBoundEntry = null;
        Agent agent = data.Kind == LocationAgentKind.Animal
            ? SpawnAnimalPuppet(data)
            : SpawnHumanPuppet(data, out rosterBoundEntry);
        if (agent == null)
        { rejected = "Location puppet is unresolvable."; return true; } // drop (already logged)

        agent.FadeIn();
        if (data.Health > 0) agent.Health = data.Health;

        registry.TryRegisterAgent(
            data.OwnerControllerId,
            data.OriginalOwnerControllerId,
            data.MovementScopeId,
            data.AgentId,
            data.MovementId,
            agent,
            data.AuthorityRevision);
        if (data.Kind == LocationAgentKind.Human && data.HasCurrentEquipment)
            data.CurrentEquipment.Apply(agent);

        var binding = new LocationAgentBinding(data.Kind, data.RosterEntry, data.ItemId, data.HarnessItemId);
        bindingMap.Record(data.AgentId, binding);

        // Every human puppet gets a REAL AgentNavigator even though it never ticks
        // (CampaignAgentComponent.OnTick gates on IsAIControlled, so it is dormant on Controller.None):
        // scene points dereference it unguarded — AnimationPoint.SetAgentItemVisibility NREs the whole
        // mission tick for a navigator-less user (SR-043) — and its ctor attaches the roster entry's
        // carry prefabs and special item through the native bookkeeping (SR-023), which adoption's
        // CreateAgentNavigator null-guard then reuses instead of stacking a second attach.
        if (data.Kind == LocationAgentKind.Human)
            EnsurePuppetNavigator(agent, rosterBoundEntry);

        // Mid-performance catch-up / a use that beat the spawn: have the puppet use its local point.
        if (data.Kind == LocationAgentKind.Human)
        {
            if (pendingPointUses.TryGetValue(data.AgentId, out var pendingPointId))
            {
                pendingPointUses.Remove(data.AgentId);
                ApplyPointUse(agent, pendingPointId, data.AgentId, resetToPointFrame: true);
            }
            else if (data.HasUsedPoint)
            {
                ApplyPointUse(agent, data.UsedPointId, data.AgentId, resetToPointFrame: true);
            }
        }

        // A retained record of a departed host can apply after the migration sweep. Every peer moves
        // the late registry entry to the current host; only that host revives its settlement AI.
        if (ownerIsRetainedFormerHost)
            authorityMigrator.ApplyLateSpawnedPuppet(agent, data.AgentId);

        Logger.Debug("[LocationSync] Spawned {Kind} puppet (agent {AgentId})", data.Kind, data.AgentId);
        slotsAvailable -= slotsNeeded;
        return true;
    }

    private Agent SpawnHumanPuppet(LocationAgentSpawnData data, out LocationCharacter entry)
    {
        entry = null;
        if (!objectManager.TryGetObjectWithLogging(data.CharacterId, out CharacterObject character))
        {
            Logger.Warning("[LocationSync] Puppet skipped: unresolved character {Char} for agent {AgentId}",
                data.CharacterId, data.AgentId);
            return null;
        }

        // SR-022: bind the puppet to a LOCAL roster entry and build from ITS AgentData — the entry's
        // origin makes the puppet visible to native bookkeeping (IsAlreadySpawned, GetLocationCharacter,
        // passage guards) and is what a promoted host re-binds AI through.
        if (data.RosterEntry != null)
            rosterBinder.TryBindOrReconstruct(data.RosterEntry, out entry);

        AgentBuildData buildData;
        string actionSetCode;
        if (entry != null)
        {
            // NoHorses mirrors every native settlement spawn (the equipment override below already
            // carries no horse, since the host's agent spawned horseless — this is the belt).
            buildData = entry.GetAgentBuildData().NoHorses(noHorses: true);
            actionSetCode = entry.ActionSetCode;
        }
        else
        {
            // No roster identity (rare): an unbound stand-in — visuals still match, native roster
            // lookups just miss it, and adoption falls back to a stationary AI (SR-031).
            buildData = new AgentBuildData(character)
                .Monster(TaleWorlds.Core.FaceGen.GetMonsterWithSuffix(character.Race, "_settlement"))
                .TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.SimpleAgentOrigin(character))
                .NoHorses(noHorses: true);
            actionSetCode = ActionSetCode.GenerateActionSetNameWithSuffix(
                buildData.AgentMonster, character.IsFemale, "_villager");
        }

        buildData
            .Team(ResolvePuppetTeam(entry))
            .InitialPosition(data.Position)
            .InitialDirection(data.Direction)
            .BodyProperties(data.BodyProperties)
            .ClothingColor1(data.ClothingColor1)
            .ClothingColor2(data.ClothingColor2)
            .Controller(AgentControllerType.None);
        if (data.SpawnEquipment != null)
            buildData.Equipment(data.SpawnEquipment);

        Agent agent;
        LocationNpcGate.SuppressCapture = true;
        try
        {
            agent = Mission.Current.SpawnAgent(buildData);
        }
        finally
        {
            LocationNpcGate.SuppressCapture = false;
        }

        // Native applies the civilian action set explicitly after SpawnAgent — mirror it, or puppets
        // animate with the default (battle-flavored) set.
        if (!string.IsNullOrEmpty(actionSetCode))
        {
            var animationSystemData = buildData.AgentMonster.FillAnimationSystemData(
                MBGlobals.GetActionSet(actionSetCode), character.GetStepSize(), hasClippingPlane: false);
            agent.SetActionSet(ref animationSystemData);
        }

        return agent;
    }

    private Agent SpawnAnimalPuppet(LocationAgentSpawnData data)
    {
        if (!objectManager.TryGetObjectWithLogging(data.ItemId, out ItemObject item))
        {
            Logger.Warning("[LocationSync] Animal puppet skipped: unresolved item {Item} for agent {AgentId}",
                data.ItemId, data.AgentId);
            return null;
        }

        ItemObject harnessItem = null;
        if (!string.IsNullOrEmpty(data.HarnessItemId))
            objectManager.TryGetObject(data.HarnessItemId, out harnessItem);

        var equipmentElement = new EquipmentElement(item);
        var harnessElement = harnessItem != null ? new EquipmentElement(harnessItem) : default;
        var position = data.Position;
        var direction = data.Direction;

        LocationNpcGate.SuppressCapture = true;
        try
        {
            return Mission.Current.SpawnMonster(equipmentElement, harnessElement, in position, in direction);
        }
        finally
        {
            LocationNpcGate.SuppressCapture = false;
        }
    }

    // The navigator ctor with a roster entry replays the native spawn tail's visual half: carry
    // prefabs (SetItemsVisibility) and the special item. An unbound stand-in still gets a BARE
    // navigator — scene points dereference it unguarded, so a navigator-less human user is a
    // mission-tick NRE waiting for its first point use.
    private static void EnsurePuppetNavigator(Agent agent, LocationCharacter entry)
    {
        var component = agent.GetComponent<CampaignAgentComponent>();
        if (component == null)
        {
            component = new CampaignAgentComponent(agent);
            agent.AddComponent(component);
        }
        if (component.AgentNavigator != null) return;

        if (entry != null) component.CreateAgentNavigator(entry);
        else component.CreateAgentNavigator();
    }

    // Native teams a settlement NPC by its roster entry's relation (MissionAgentHandler): Neutral →
    // no team, Friendly → player ally, Enemy → player enemy.
    private static Team ResolvePuppetTeam(LocationCharacter entry)
    {
        if (entry == null) return Team.Invalid;
        return entry.CharacterRelation switch
        {
            LocationCharacter.CharacterRelations.Friendly => Mission.Current.PlayerAllyTeam ?? Team.Invalid,
            LocationCharacter.CharacterRelations.Enemy => Mission.Current.PlayerEnemyTeam ?? Team.Invalid,
            _ => Team.Invalid,
        };
    }

    public void NotifyMissionReady()
    {
        missionReady = true;
    }

    public bool HasPendingNpcRecords
    {
        get { lock (pendingPuppetLock) return pendingPuppets.Count > 0; }
    }

    public void DrainPendingPuppets()
    {
        if (disposed || !missionReady || Mission.Current == null) return;

        PendingSpawn[] pending;
        lock (pendingPuppetLock)
        {
            if (pendingPuppets.Count == 0) return;
            int count = Math.Min(MaxBufferedSpawnsPerTick, pendingPuppets.Count);
            pending = new PendingSpawn[count];
            pendingPuppets.CopyTo(0, pending, 0, count);
            pendingPuppets.RemoveRange(0, count);
        }

        // Count the live remaining capacity ONCE for the whole drain and decrement as puppets spawn.
        int slotsAvailable;
        try { slotsAvailable = agentBudget.RemainingCapacity(agentBudget.CountLiveAgents(Mission.Current)); }
        catch (Exception error)
        {
            try { CompleteCancelled(pending, error.ToString()); }
            catch (Exception secondary)
            { Logger.Error(secondary, "Location pending budget failure reporting failed after {ApplyError}", error); }
            throw;
        }

        foreach (var record in pending)
        {
            // One bad record must not abort the whole drain: drop on failure, never re-buffer it.
            try
            {
                if (!TryApplyPending(record, ref slotsAvailable)) QueuePending(record);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[LocationSync] Failed to spawn buffered puppet {AgentId}; dropping it", record.Data.AgentId);
            }
        }
    }

    private void Handle_PeerEntered(MessagePayload<NetworkMissionPeerEntered> payload)
    {
        if (payload.What.InstanceId != null && payload.What.InstanceId != session.InstanceId) return;
        GameThread.RunSafe(
            () => withdrawalState.MarkEntered(payload.What.ControllerId),
            context: nameof(Handle_PeerEntered));
    }

    private void Handle_PeerLeft(MessagePayload<MissionPeerLeft> payload)
    {
        QueueControllerWithdrawn(payload.What.ControllerId, payload.What.InstanceId);
    }

    private void Handle_PeerDisconnected(MessagePayload<MissionPeerDisconnected> payload)
    {
        QueueControllerWithdrawn(payload.What.ControllerId, payload.What.InstanceId);
    }

    // A departed PLAYER's not-yet-applied records are stale (its puppet despawns, nothing adopts
    // it) and are pruned. A departed HOST's records are the crowd itself and are RETAINED — the
    // promoted successor adopts the already-spawned subset via the migration sweep and each retained
    // record as it spawns (late adoption), so a partially-applied catch-up never loses NPCs and an
    // empty binding map cannot misread a promotion as a fresh election (SR-014).
    private void QueueControllerWithdrawn(string controllerId, string instanceId)
    {
        if (string.IsNullOrEmpty(controllerId)) return;
        if (instanceId != null && instanceId != session.InstanceId) return;

        // Read the host role BEFORE the promotion broadcast replaces the assignment (the departure
        // fan-out precedes it on the relay).
        bool wasHost = session.IsHostController(controllerId);

        GameThread.RunSafe(
            () => MarkControllerWithdrawn(controllerId, wasHost),
            context: nameof(QueueControllerWithdrawn));
    }

    private void MarkControllerWithdrawn(string controllerId, bool wasHost)
    {

        withdrawalState.MarkWithdrawn(controllerId, wasHost);

        if (wasHost)
        {
            lock (pendingPuppetLock)
            {
                foreach (var pending in pendingPuppets)
                {
                    var data = pending.Data;
                    if (data.OwnerControllerId == controllerId)
                        withdrawalState.RetainFormerHostRecord(data.AgentId);
                }
            }
        }
        else
        {
            CancelPending(data => data.OwnerControllerId == controllerId,
                "Location puppet player withdrew before pending apply.");
        }
    }

    private bool IsWithdrawn(string controllerId, out bool wasHost)
        => withdrawalState.IsWithdrawn(controllerId, out wasHost);

    private bool IsTombstoned(Guid agentId)
    {
        lock (tombstoneLock)
        {
            return despawnedAgentIds.Contains(agentId);
        }
    }
}
