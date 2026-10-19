using Common;
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
    private long appliedEpoch;
    private long appliedSequence;
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
        ICampaignMovementCheckpointService movement = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        this.controllerIdProvider = controllerIdProvider;
        this.objectManager = objectManager;
        this.rosterCheckpointService = rosterCheckpointService;
        this.mapTimeTracker = mapTimeTracker;
        this.sessionCheckpointService = sessionCheckpointService;
        this.schedulerCheckpointService = schedulerCheckpointService;
        this.asyncTasks = asyncTasks;
        this.movement = movement;
        messageBroker.Subscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrier>(Handle_HandoffBarrier);
    }

    public void Dispose()
    {
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
        var barrier = payload.What;

        // All receive-side campaign applies that defer to GameThread.EnqueueSafe are ahead of this
        // acknowledgement. Reading session and campaign readiness here also avoids polling-thread reads.
        GameThread.EnqueueSafe(() => ApplyBarrierOnGameThread(barrier), context: nameof(CampaignAuthorityHandoffBarrierClientHandler));
    }

    private void ApplyBarrierOnGameThread(NetworkCampaignAuthorityHandoffBarrier barrier)
    {
        if (!campaignSynchronized || !mapReady ||
            !sessionIdentity.TryGetConfirmedLease(out var lease) || !lease.IsPaused ||
            !Matches(barrier, lease, controllerIdProvider.ControllerId))
        {
            Logger.Warning("Ignored campaign authority handoff barrier {BarrierSequence} for controller {ControllerId}; " +
                "campaign sync, map readiness, or paused lease did not match",
                barrier.BarrierSequence, controllerIdProvider.ControllerId);
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


        if (!objectManager.TryApplyAllocationCheckpoint(barrier.AllocationCheckpoint))
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; registry allocation checkpoint is missing or invalid",
                barrier.BarrierSequence);
            return;
        }

        if (!rosterCheckpointService.TryApply(barrier.RosterCheckpoint))
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; world rosters did not match the checkpoint",
                barrier.BarrierSequence);
            return;
        }

        if (!sessionCheckpointService.TryApply(barrier.SessionCheckpoint))
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; co-op session or registered players did not match",
                barrier.BarrierSequence);
            return;
        }

        if (!schedulerCheckpointService.TryApply(barrier.SchedulerCheckpoint))
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; native campaign scheduler did not match",
                barrier.BarrierSequence);
            return;
        }

        if (!mapTimeTracker.TryApplyAuthorityHandoffBaseline(barrier.ClockCheckpoint.Ticks))
        {
            Logger.Warning("Rejected campaign handoff barrier {BarrierSequence}; final campaign clock could not be applied",
                barrier.BarrierSequence);
            return;
        }

        if (installMovement != null && !installMovement()) return;
        appliedEpoch = lease.Epoch;
        appliedSequence = barrier.BarrierSequence;
        network.SendAll(barrier.ToApplied());
        network.FlushPendingMessages();
        Logger.Information("Applied campaign authority handoff barrier {BarrierSequence} for paused epoch {Epoch}",
            barrier.BarrierSequence, barrier.PausedEpoch);
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
