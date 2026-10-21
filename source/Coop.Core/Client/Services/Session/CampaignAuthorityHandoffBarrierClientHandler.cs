using Common;
using GameInterface.Services.Missions;
using LiteNetLib;
using System.Threading;
using GameInterface.Services.CampaignAuthority.Movement;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Client.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Time.Messages;
using Serilog;
using System;

namespace Coop.Core.Client.Services.Session;

/// <summary>
/// Acknowledges an authority handoff barrier only after previously received ordered updates have
/// reached the game-thread apply queue and the local campaign is still safe to simulate.
/// </summary>
public sealed class CampaignAuthorityHandoffBarrierClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityHandoffBarrierClientHandler>();

    private readonly CampaignApplyCompletion.Binding applyBinding;
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IObjectManager objectManager;
    private readonly ICampaignRosterCheckpointService rosterCheckpointService;
    private readonly IMapTimeTrackerInterface mapTimeTracker;
    private readonly ICampaignSessionCheckpointService sessionCheckpointService;
    private readonly ICampaignSchedulerCheckpointService schedulerCheckpointService;
    private readonly ICampaignAsyncTaskDrain asyncTasks;
    private readonly ICampaignMovementCheckpointService movement;
    private readonly IRoomMissionFactsView missionFacts;
    private NetworkCampaignAuthorityHandoffBarrier? deferredBarrier;
    private NetPeer deferredPeer;
    private int continuationQueued, disposed;
    private long appliedEpoch;
    private long appliedSequence;
    private long installedEpoch;
    private long installedSequence;
    private bool campaignSynchronized;
    private bool mapReady;

    public CampaignAuthorityHandoffBarrierClientHandler(
        IMessageBroker messageBroker,
        INetwork network,
        ISessionIdentity sessionIdentity,
        IControllerIdProvider controllerIdProvider,
        IObjectManager objectManager,
        ICampaignRosterCheckpointService rosterCheckpointService,
        IMapTimeTrackerInterface mapTimeTracker,
        ICampaignSessionCheckpointService sessionCheckpointService,
        ICampaignSchedulerCheckpointService schedulerCheckpointService,
        ICampaignAsyncTaskDrain asyncTasks,
        ICampaignMovementCheckpointService movement = null, IRoomMissionFactsView missionFacts = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        applyBinding = CampaignApplyCompletion.Attach(messageBroker, sessionIdentity);
        this.controllerIdProvider = controllerIdProvider;
        this.objectManager = objectManager;
        this.rosterCheckpointService = rosterCheckpointService;
        this.mapTimeTracker = mapTimeTracker;
        this.sessionCheckpointService = sessionCheckpointService;
        this.schedulerCheckpointService = schedulerCheckpointService;
        this.asyncTasks = asyncTasks;
        this.movement = movement;
        this.missionFacts = missionFacts;
        if (missionFacts != null) missionFacts.FactsChanged += OnFactsChanged;
        messageBroker.Subscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrier>(Handle_HandoffBarrier);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        applyBinding?.Dispose();
        deferredBarrier = null;
        if (missionFacts != null) missionFacts.FactsChanged -= OnFactsChanged;
        messageBroker.Unsubscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityHandoffBarrier>(Handle_HandoffBarrier);
    }

    private void Handle_ClientCampaignReady(MessagePayload<ClientCampaignReady> _)
    {
        campaignSynchronized = true;
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        mapReady = payload.What.IsMapReady;
    }

    private void Handle_HandoffBarrier(MessagePayload<NetworkCampaignAuthorityHandoffBarrier> payload)
    {
        var peer = payload.Who as NetPeer;
        if (!IsCurrentRoomPeer(peer)) return;
        var barrier = payload.What;

        // All receive-side campaign applies that defer to GameThread.EnqueueSafe are ahead of this
        // acknowledgement. Reading session and campaign readiness here also avoids polling-thread reads.
        GameThread.EnqueueSafe(() =>
        {
            try { ApplyBarrierOnGameThread(barrier, peer); }
            catch (Exception ex)
            {
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(sessionIdentity.CurrentLease,
                    "Critical handoff checkpoint installation threw: " + ex);
                throw;
            }
        }, context: nameof(CampaignAuthorityHandoffBarrierClientHandler));
    }

    private bool IsCurrentRoomPeer(NetPeer peer) => Volatile.Read(ref disposed) == 0 && peer != null &&
        network is IRoomHostPeerProvider provider && provider.IsCurrentRoomHostPeer(peer);

    private void OnFactsChanged()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref continuationQueued, 1) != 0) return;
        GameThread.EnqueueSafe(() =>
        {
            Interlocked.Exchange(ref continuationQueued, 0);
            if (!deferredBarrier.HasValue || !IsCurrentRoomPeer(deferredPeer)) return;
            var barrier = deferredBarrier.Value;
            var peer = deferredPeer;
            deferredBarrier = null;
            try { ApplyBarrierOnGameThread(barrier, peer); }
            catch (Exception exception)
            {
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(sessionIdentity.CurrentLease,
                    "Deferred handoff checkpoint installation threw: " + exception);
                throw;
            }
        }, context: nameof(CampaignAuthorityHandoffBarrierClientHandler));
    }

    private void ApplyBarrierOnGameThread(NetworkCampaignAuthorityHandoffBarrier barrier, NetPeer peer)
    {
        if (!IsCurrentRoomPeer(peer)) return;
        if (applyBinding?.IsActive == false) return;
        if (!campaignSynchronized || !mapReady ||
            !sessionIdentity.TryGetConfirmedLease(out var lease) || !lease.IsPaused ||
            !Matches(barrier, lease, controllerIdProvider.ControllerId))
        {
            Logger.Warning("Ignored campaign authority handoff barrier {BarrierSequence} for controller {ControllerId}; " +
                "campaign sync, map readiness, or paused lease did not match",
                barrier.BarrierSequence, controllerIdProvider.ControllerId);
            return;
        }

        if (missionFacts == null || !barrier.MissionFactsVersion.IsValid || barrier.MissionFactsVersion.RoomId != lease.RoomId)
        {
            Logger.Warning("Refused handoff barrier {BarrierSequence}: room Mission membership version missing", barrier.BarrierSequence);
            return;
        }
        if (!missionFacts.HasApplied(barrier.MissionFactsVersion))
        {
            deferredBarrier = barrier;
            deferredPeer = peer;
            return;
        }
        if (!missionFacts.CanActivateCampaign(barrier.MissionFactsVersion))
        {
            Logger.Warning("Refused handoff barrier {BarrierSequence}: occupied Mission protection facts are not implemented", barrier.BarrierSequence);
            return;
        }
        if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out var applyReason))
        {
            Logger.Warning("Refused handoff barrier {BarrierSequence}: {ApplyReason}", barrier.BarrierSequence, applyReason);
            return;
        }

        if (barrier.ClockCheckpoint == null || barrier.ClockCheckpoint.Ticks < 0)
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; final campaign clock is missing or invalid",
                barrier.BarrierSequence);
            return;
        }

        if (barrier.SchedulerCheckpoint == null || !asyncTasks.TryDrain()) return;
        if (lease.Epoch == appliedEpoch && barrier.BarrierSequence <= appliedSequence) return;
        Func<bool> installMovement = null;
        if (movement != null && (barrier.MovementCheckpoint == null ||
            !barrier.MovementCheckpoint.Precedes(lease) ||
            movement.LastApplied == null || movement.LastApplied.SourceLease != barrier.MovementCheckpoint.SourceLease ||
            movement.LastApplied.Ticks != barrier.ClockCheckpoint.Ticks || movement.LastApplied.Sha256 != barrier.MovementCheckpoint.Sha256 ||
            !movement.TryPrepare(barrier.MovementCheckpoint, barrier.MovementCheckpoint.SourceLease,
                barrier.ClockCheckpoint.Ticks, out installMovement))) return;


        if (installedEpoch != lease.Epoch || installedSequence != barrier.BarrierSequence)
        {
            using (CampaignApplyCompletion.For(sessionIdentity).Observe(typeof(NetworkCampaignAuthorityHandoffBarrier)))
            using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
            {
                if (!TryInstallCheckpoints(barrier, lease, installMovement)) return;
                installedEpoch = lease.Epoch;
                installedSequence = barrier.BarrierSequence;
            }
        }
        if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out applyReason))
        {
            Logger.Warning("Refused installed handoff barrier {BarrierSequence}: {ApplyReason}", barrier.BarrierSequence, applyReason);
            return;
        }
        if (!IsCurrentRoomPeer(peer) || !missionFacts.CanActivateCampaign(barrier.MissionFactsVersion))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease,
                "Room Mission facts or room host peer changed during handoff installation.");
            return;
        }
        appliedEpoch = lease.Epoch;
        appliedSequence = barrier.BarrierSequence;
        network.SendAll(barrier.ToApplied());
        network.FlushPendingMessages();
        Logger.Information("Applied campaign authority handoff barrier {BarrierSequence} for paused epoch {Epoch}",
            barrier.BarrierSequence, barrier.PausedEpoch);
    }

    private bool TryInstallCheckpoints(NetworkCampaignAuthorityHandoffBarrier barrier, AuthorityLease lease, Func<bool> installMovement)
    {
        if (!objectManager.TryApplyAllocationCheckpoint(barrier.AllocationCheckpoint))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Registry allocation checkpoint refused.");
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; registry allocation checkpoint is missing or invalid",
                barrier.BarrierSequence);
            return false;
        }

        if (!rosterCheckpointService.TryApply(barrier.RosterCheckpoint))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "World roster checkpoint refused.");
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; world rosters did not match the checkpoint",
                barrier.BarrierSequence);
            return false;
        }

        if (!sessionCheckpointService.TryApply(barrier.SessionCheckpoint))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Shared session checkpoint refused.");
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; co-op session or registered players did not match",
                barrier.BarrierSequence);
            return false;
        }

        if (!schedulerCheckpointService.TryApply(barrier.SchedulerCheckpoint))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Native scheduler checkpoint refused.");
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; native campaign scheduler did not match",
                barrier.BarrierSequence);
            return false;
        }

        if (!mapTimeTracker.TryApplyAuthorityHandoffBaseline(barrier.ClockCheckpoint.Ticks))
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "World clock checkpoint refused.");
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; final campaign clock could not be applied",
                barrier.BarrierSequence);
            return false;
        }

        if (installMovement != null && !installMovement())
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Final movement installation refused.");
            return false;
        }
        return true;
    }

    internal static bool Matches(
        NetworkCampaignAuthorityHandoffBarrier barrier,
        AuthorityLease lease,
        string localControllerId) =>
        lease.IsValid && lease.IsPaused &&
        !string.IsNullOrWhiteSpace(localControllerId) &&
        barrier.BarrierSequence > 0 && barrier.PausedEpoch == lease.Epoch &&
        string.Equals(barrier.RoomId, lease.RoomId, StringComparison.Ordinal) &&
        string.Equals(barrier.PausedAuthorityId, lease.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(barrier.CandidateControllerId, localControllerId, StringComparison.Ordinal);
}
