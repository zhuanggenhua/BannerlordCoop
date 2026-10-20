using Common;
using GameInterface.Services.CampaignAuthority.Movement;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Session;
using Common.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Time.Interfaces;
using Serilog;

namespace Coop.Core.Client.Services.Session;

/// <summary>Stops a remote authority's local writes before it acknowledges a room-host handoff.</summary>
public sealed class CampaignAuthorityFreezeClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityFreezeClientHandler>();

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
    private readonly ISendCoalescer sendCoalescer;
    private readonly ICampaignMovementCheckpointService movement;
    private readonly ICampaignMovementFreezePreparation movementPreparation;

    public CampaignAuthorityFreezeClientHandler(
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
        ISendCoalescer sendCoalescer = null,
        ICampaignMovementCheckpointService movement = null,
        ICampaignMovementFreezePreparation movementPreparation = null)
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
        this.sendCoalescer = sendCoalescer;
        this.movement = movement;
        this.movementPreparation = movementPreparation;
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeRequest>(Handle_FreezeRequest);
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeCanceled>(Handle_FreezeCanceled);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeRequest>(Handle_FreezeRequest);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeCanceled>(Handle_FreezeCanceled);
    }

    private void Handle_FreezeRequest(MessagePayload<NetworkCampaignAuthorityFreezeRequest> payload)
    {
        var request = payload.What;
        GameThread.EnqueueSafe(() => ApplyFreezeRequest(request), context: nameof(CampaignAuthorityFreezeClientHandler));
    }

    private void ApplyFreezeRequest(NetworkCampaignAuthorityFreezeRequest request)
    {
        if (!sessionIdentity.TryGetConfirmedLease(out var lease) ||
            !Matches(request, lease, controllerIdProvider.ControllerId))
        {
            Logger.Warning("Ignored campaign authority freeze request {HandoffSequence} for controller {ControllerId}; the request did not match the active lease",
                request.HandoffSequence, controllerIdProvider.ControllerId);
            return;
        }

        try
        {
            if (sessionIdentity.IsLocallyFrozen(lease))
            {
                if (!sessionIdentity.TryFreezeLocalAuthority(lease, request.HandoffSequence)) return;
            }
            else
            {
                // Worker callbacks resolve the current lease while finishing. Waiting inside the
                // lease lock would deadlock those callbacks against the game thread.
                if (!asyncTasks.TryDrain()) return;
                bool frozen = false;
                if (!sessionIdentity.TryExecuteWithLease(lease, () =>
                {
                    if (movementPreparation != null && !movementPreparation.TryPrepareForFreeze(lease)) return;
                    sendCoalescer?.Flush(network);
                    frozen = sessionIdentity.TryFreezeLocalAuthority(lease, request.HandoffSequence);
                }) || !frozen) return;
            }

            // Fence local simulation before flushing. The acknowledgement follows every already queued
            // authority delta on this reliable-ordered connection.
            network.FlushPendingMessages();
            if (!rosterCheckpointService.TryCapture(out var rosterCheckpoint))
            {
                Logger.Warning("Could not capture world rosters for freeze {HandoffSequence}; local writes remain frozen without acknowledgement",
                    request.HandoffSequence);
                return;
            }
            if (!mapTimeTracker.TryGetCurrentTicks(out long ticks) || ticks < 0)
            {
                Logger.Warning("Could not capture final clock for freeze {HandoffSequence}; local writes remain frozen without acknowledgement",
                    request.HandoffSequence);
                return;
            }
            if (!sessionCheckpointService.TryCapture(out var sessionCheckpoint))
            {
                Logger.Warning("Could not capture co-op session for freeze {HandoffSequence}; local writes remain frozen without acknowledgement",
                    request.HandoffSequence);
                return;
            }
            if (!schedulerCheckpointService.TryCapture(out var schedulerCheckpoint))
            {
                Logger.Warning("Could not capture campaign scheduler for freeze {HandoffSequence}; local writes remain frozen without acknowledgement",
                    request.HandoffSequence);
                return;
            }
            CampaignMovementCheckpoint movementCheckpoint = null;
            if (movement != null && !movement.TryCapture(lease, ticks, out movementCheckpoint))
            {
                Logger.Warning("Could not capture final movement for freeze {HandoffSequence}; authority remains frozen", request.HandoffSequence);
                return;
            }
            network.SendAll(request.ToAcknowledged(objectManager.CaptureAllocationCheckpoint(), rosterCheckpoint,
                new CampaignClockCheckpoint(ticks), sessionCheckpoint, schedulerCheckpoint, movementCheckpoint));
            network.FlushPendingMessages();
            Logger.Information("Froze local campaign writes and acknowledged handoff {HandoffSequence} at epoch {Epoch} to controller {CandidateControllerId}",
                request.HandoffSequence, request.Epoch, request.CandidateControllerId);
        }
        catch (System.Exception ex)
        {
            Logger.Error(ex, "Failed to acknowledge campaign authority freeze {HandoffSequence}",
                request.HandoffSequence);
        }
    }

    private void Handle_FreezeCanceled(MessagePayload<NetworkCampaignAuthorityFreezeCanceled> payload)
    {
        var canceled = payload.What;
        var lease = new AuthorityLease(
            canceled.RoomId, canceled.AuthorityId, canceled.AuthorityControllerId, canceled.Epoch);
        GameThread.EnqueueSafe(() =>
        {
            if (!string.Equals(canceled.AuthorityControllerId, controllerIdProvider.ControllerId, System.StringComparison.Ordinal) ||
                !sessionIdentity.TryCancelLocalAuthorityFreeze(lease, canceled.HandoffSequence))
                return;

            Logger.Information("Canceled campaign authority freeze {HandoffSequence}; epoch {Epoch} remains active",
                canceled.HandoffSequence, canceled.Epoch);
        }, context: nameof(CampaignAuthorityFreezeClientHandler));
    }

    internal static bool Matches(
        NetworkCampaignAuthorityFreezeRequest request,
        AuthorityLease currentLease,
        string localControllerId) =>
        request.HandoffSequence > 0 &&
        !string.IsNullOrWhiteSpace(request.CandidateControllerId) &&
        !string.Equals(request.CandidateControllerId, request.AuthorityControllerId, System.StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(localControllerId) &&
        currentLease.IsValid && !currentLease.IsPaused &&
        string.Equals(request.RoomId, currentLease.RoomId, System.StringComparison.Ordinal) &&
        string.Equals(request.AuthorityId, currentLease.AuthorityId, System.StringComparison.Ordinal) &&
        string.Equals(request.AuthorityControllerId, currentLease.AuthorityControllerId, System.StringComparison.Ordinal) &&
        request.Epoch == currentLease.Epoch &&
        string.Equals(request.AuthorityControllerId, localControllerId, System.StringComparison.Ordinal);
}
