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
using System.Runtime.CompilerServices;

namespace Coop.Core.Client.Services.Session;

/// <summary>Stops a remote authority's local writes before it acknowledges a room-host handoff.</summary>
public sealed class CampaignAuthorityFreezeClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityFreezeClientHandler>();

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
    private readonly ISendCoalescer sendCoalescer;
    private readonly ICampaignMovementCheckpointService movement;
    private readonly ICampaignMovementFreezePreparation movementPreparation;
    private static readonly ConditionalWeakTable<ISessionIdentity, FreezeAckAttempt> attempts = new();

    private sealed class FreezeAckAttempt
    {
        internal AuthorityLease Lease;
        internal long Sequence;
        internal string Candidate, SendState;
        internal bool AckAttempted, Canceled, Processing;
    }

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
        applyBinding = CampaignApplyCompletion.Attach(messageBroker, sessionIdentity);
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
        applyBinding?.Dispose();
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
        if (applyBinding?.IsActive == false) return;
        if (!sessionIdentity.TryGetConfirmedLease(out var lease) ||
            !Matches(request, lease, controllerIdProvider.ControllerId))
        {
            Logger.Warning("Ignored campaign authority freeze request {HandoffSequence} for controller {ControllerId}; the request did not match the active lease",
                request.HandoffSequence, controllerIdProvider.ControllerId);
            return;
        }

        var previous = attempts.GetValue(sessionIdentity, _ => new FreezeAckAttempt());
        if (previous.Lease == lease && (request.HandoffSequence < previous.Sequence ||
            (request.HandoffSequence == previous.Sequence &&
                (request.CandidateControllerId != previous.Candidate || previous.AckAttempted || previous.Canceled || previous.Processing))))
        {
            Logger.Information("Ignored duplicate or superseded freeze {HandoffSequence} room {RoomId} authority {AuthorityId} epoch {Epoch} candidate {CandidateControllerId}; recorded sequence {RecordedSequence} candidate {RecordedCandidate} state {AckState} canceled {Canceled}; delivery remains unconfirmed",
                request.HandoffSequence, lease.RoomId, lease.AuthorityId, lease.Epoch, request.CandidateControllerId,
                previous.Sequence, previous.Candidate, previous.SendState, previous.Canceled);
            return;
        }
        FreezeAckAttempt attempt = null;
        string stage = "freeze";
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

            attempt = previous;
            if (attempt.Lease != lease || attempt.Sequence != request.HandoffSequence)
            {
                attempt.Lease = lease;attempt.Sequence = request.HandoffSequence;attempt.Candidate = request.CandidateControllerId;
                attempt.AckAttempted = false;attempt.Canceled = false;attempt.SendState = "capture-not-sent";
            }
            attempt.Processing = true;

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
            if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out var applyReason))
            {
                Logger.Warning("Refused frozen authority ACK {HandoffSequence}: {ApplyReason}", request.HandoffSequence, applyReason);
                return;
            }
            stage = "allocation-capture";
            var ack = request.ToAcknowledged(objectManager.CaptureAllocationCheckpoint(), rosterCheckpoint,
                new CampaignClockCheckpoint(ticks), sessionCheckpoint, schedulerCheckpoint, movementCheckpoint);
            if (!sessionIdentity.TryGetConfirmedLease(out var current) || current != lease ||
                !sessionIdentity.IsLocallyFrozen(lease) || attempt.Canceled)
            {
                Logger.Warning("Refused obsolete freeze ACK {HandoffSequence} at epoch {Epoch}; current lease or local fence changed during capture",
                    request.HandoffSequence, request.Epoch);
                return;
            }
            stage = "send-call";
            LogAckSendStage(stage, request, ticks, movementCheckpoint);
            attempt.AckAttempted = true;attempt.SendState = "send-call-uncertain";
            network.SendAll(ack);
            stage = "send-call-returned";
            attempt.SendState = "send-returned-delivery-unconfirmed";
            LogAckSendStage(stage, request, ticks, movementCheckpoint);
            stage = "flush-call";
            network.FlushPendingMessages();
            stage = "flush-call-returned-delivery-unconfirmed";
            attempt.SendState = stage;
            LogAckSendStage(stage, request, ticks, movementCheckpoint);
            Logger.Information("Froze local campaign writes and completed freeze ACK send/flush calls for handoff {HandoffSequence} at epoch {Epoch} to controller {CandidateControllerId}; delivery is unconfirmed",
                request.HandoffSequence, request.Epoch, request.CandidateControllerId);
        }
        catch (System.Exception ex)
        {
            if (attempt?.AckAttempted == true) attempt.SendState = "send-or-flush-uncertain-safe-frozen";
            Logger.Error(ex, "Failed to acknowledge campaign authority freeze {HandoffSequence} at stage {Stage} room {RoomId} authority {AuthorityId} epoch {Epoch}",
                request.HandoffSequence, stage, request.RoomId, request.AuthorityId, request.Epoch);
            if (attempt?.AckAttempted == true)
                Logger.Warning("Freeze ACK {HandoffSequence} transport outcome uncertain; authority remains safely frozen and automatic re-enqueue/reconnect recovery is not implemented",
                    request.HandoffSequence);
        }
        finally { if (attempt != null) attempt.Processing = false; }
    }

    private void LogAckSendStage(string stage, NetworkCampaignAuthorityFreezeRequest request, long ticks,
        CampaignMovementCheckpoint movementCheckpoint)
    {
        string peers = network is CoopClient client ? client.DescribeRoomPeers() : "Unavailable(non-CoopClient transport)";
        Logger.Information("Freeze ACK send stage {Stage} handoff {HandoffSequence} room {RoomId} authority {AuthorityId} writer {WriterControllerId} epoch {Epoch} candidate {CandidateControllerId} ticks {Ticks} movementParties {MovementParties} movementHash {MovementHash} roomPeers {RoomPeers}; transport snapshot only, delivery unconfirmed",
            stage, request.HandoffSequence, request.RoomId, request.AuthorityId, request.AuthorityControllerId,
            request.Epoch, request.CandidateControllerId, ticks, movementCheckpoint?.PartyCount, movementCheckpoint?.Sha256, peers);
    }

    private void Handle_FreezeCanceled(MessagePayload<NetworkCampaignAuthorityFreezeCanceled> payload)
    {
        var canceled = payload.What;
        var lease = new AuthorityLease(
            canceled.RoomId, canceled.AuthorityId, canceled.AuthorityControllerId, canceled.Epoch);
        GameThread.EnqueueSafe(() =>
        {
            if (applyBinding?.IsActive == false) return;
            attempts.TryGetValue(sessionIdentity, out var attempt);
            if (attempt != null && attempt.Lease == lease && attempt.Sequence == canceled.HandoffSequence &&
                attempt.Candidate != canceled.CandidateControllerId) return;
            if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out var applyReason))
            {
                Logger.Warning("Refused to cancel authority freeze {HandoffSequence}: {ApplyReason}", canceled.HandoffSequence, applyReason);
                return;
            }
            if (!string.Equals(canceled.AuthorityControllerId, controllerIdProvider.ControllerId, System.StringComparison.Ordinal) ||
                !sessionIdentity.TryCancelLocalAuthorityFreeze(lease, canceled.HandoffSequence))
                return;

            attempt = attempts.GetValue(sessionIdentity, _ => new FreezeAckAttempt());
            attempt.Lease = lease;attempt.Sequence = canceled.HandoffSequence;attempt.Candidate = canceled.CandidateControllerId;
            attempt.Canceled = true;attempt.Processing = false;attempt.SendState = "canceled";
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
