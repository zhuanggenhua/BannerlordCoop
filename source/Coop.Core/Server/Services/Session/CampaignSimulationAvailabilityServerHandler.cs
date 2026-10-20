using Common;
using GameInterface.Services.CampaignAuthority.Movement;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Time.Messages;
using GameInterface.Services.Time.Interfaces;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;
using Coop.Core.Server.States.Messages;

namespace Coop.Core.Server.Services.Session;

public sealed class CampaignSimulationAvailabilityServerHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSimulationAvailabilityServerHandler>();
    private static long nextBarrierSequence;

    private readonly CampaignApplyCompletion.Binding applyBinding;
    private readonly IMessageBroker messageBroker;
    private readonly ICampaignAuthorityAvailabilityCoordinator coordinator;
    private readonly IConnectionCollection connections;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IObjectManager objectManager;
    private readonly ISendCoalescer sendCoalescer;
    private readonly ICampaignRosterCheckpointService rosterCheckpointService;
    private readonly IMapTimeTrackerInterface mapTimeTracker;
    private readonly ICampaignSessionCheckpointService sessionCheckpointService;
    private readonly ICampaignSchedulerCheckpointService schedulerCheckpointService;
    private readonly ICampaignAsyncTaskDrain asyncTasks;
    private readonly ICampaignMovementCheckpointService movement;
    private readonly ICampaignMovementFreezePreparation movementPreparation;
    private CampaignMovementCheckpoint finalMovementCheckpoint;
    private AuthorityLease pendingMovementLease;
    private readonly Dictionary<int, (NetPeer peer, string controller)> pendingMovementAcks = new();
    public int PendingMovementAcknowledgements => pendingMovementAcks.Count;
    private readonly object peerGate = new object();
    private readonly object localReportGate = new object();
    private readonly object selectionGate = new object();
    private readonly object leaseGate = new object();
    private readonly Dictionary<int, string> peerControllerIds = new Dictionary<int, string>();
    private readonly Timer reconciliationTimer;
    private bool campaignReady;
    private bool campaignInitializationComplete;
    private bool mapReady;
    private long localRevision;
    private bool hasLoggedSelection;
    private CampaignAuthoritySelection lastLoggedSelection;
    private AuthorityLease? pendingLeaseBroadcast;
    private long pendingBarrierSequence;
    private long pendingBarrierEpoch;
    private string pendingBarrierAuthorityId = string.Empty;
    private string pendingBarrierCandidateControllerId = string.Empty;
    private NetPeer pendingBarrierCandidatePeer;
    private bool hasPendingBarrier;
    private bool pendingBarrierAcknowledged;
    private AuthorityLease pendingFreezeLease;
    private string pendingFreezeCandidateControllerId = string.Empty;
    private long pendingFreezeSequence;
    private bool hasPendingFreeze;
    private int heartbeatQueued;
    private int leaseReconciliationQueued;
    private int disposed;
    private sealed class VerifiedFreezeAck
    {
        internal NetPeer Peer;
        internal NetworkCampaignAuthorityFreezeAcknowledged Ack;
        internal AuthorityLease Lease;
        internal string Fingerprint;
        internal bool Installed;
    }
    private VerifiedFreezeAck retainedFreezeAck;

    private static string FreezeAckFingerprint(NetworkCampaignAuthorityFreezeAcknowledged ack)
    {
        // Local evidence fingerprint only; the original network format/serializer is unchanged.
        var json = System.Text.Json.JsonSerializer.Serialize(ack, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json))).Replace("-", "");
    }
    private void ResumeRetainedFreezeAck()
    {
        var retained = retainedFreezeAck;
        if (retained == null || Volatile.Read(ref disposed) != 0) return;
        if (!hasPendingFreeze || pendingFreezeLease != retained.Lease || pendingFreezeSequence != retained.Ack.HandoffSequence ||
            pendingFreezeCandidateControllerId != retained.Ack.CandidateControllerId || sessionIdentity.CurrentLease != retained.Lease ||
            !TryGetSynchronizedPeer(retained.Lease.AuthorityControllerId, out var currentPeer) || currentPeer != retained.Peer)
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(retained.Lease, "Retained freeze ACK lost its current lease, peer or request binding.");
            retainedFreezeAck = null;
            return;
        }
        if (CampaignApplyCompletion.For(sessionIdentity).GetStatusThrough(retained.Lease, out _) != CampaignApplyStatus.Ready) return;
        try { ApplyFreezeAcknowledgement(retained.Peer, retained.Ack); } // Revalidate full binding/fingerprint.
        catch (Exception ex)
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(retained.Lease, "Retained final checkpoint continuation threw: " + ex);
            retainedFreezeAck = null;
            throw;
        }
    }

    public CampaignSimulationAvailabilityServerHandler(
        IMessageBroker messageBroker,
        ICampaignAuthorityAvailabilityCoordinator coordinator,
        IConnectionCollection connections,
        IPlayerManager playerManager,
        IControllerIdProvider controllerIdProvider,
        INetwork network = null,
        ISessionIdentity sessionIdentity = null,
        IObjectManager objectManager = null,
        ISendCoalescer sendCoalescer = null,
        ICampaignRosterCheckpointService rosterCheckpointService = null,
        IMapTimeTrackerInterface mapTimeTracker = null,
        ICampaignSessionCheckpointService sessionCheckpointService = null,
        ICampaignSchedulerCheckpointService schedulerCheckpointService = null,
        ICampaignAsyncTaskDrain asyncTasks = null,
        ICampaignMovementCheckpointService movement = null,
        ICampaignMovementFreezePreparation movementPreparation = null)
    {
        this.messageBroker = messageBroker;
        this.coordinator = coordinator;
        this.connections = connections;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        applyBinding = CampaignApplyCompletion.Attach(messageBroker, sessionIdentity);
        this.objectManager = objectManager;
        this.sendCoalescer = sendCoalescer;
        this.rosterCheckpointService = rosterCheckpointService;
        this.mapTimeTracker = mapTimeTracker;
        this.sessionCheckpointService = sessionCheckpointService;
        this.schedulerCheckpointService = schedulerCheckpointService;
        this.asyncTasks = asyncTasks;
        this.movement = movement;
        this.movementPreparation = movementPreparation;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<CampaignInitializationCompleted>(Handle_CampaignInitializationCompleted);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeAcknowledged>(Handle_FreezeAcknowledged);
        messageBroker.Subscribe<NetworkCampaignMovementBaselineApplied>(Handle_MovementBaselineApplied);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        reconciliationTimer = new Timer(_ => OnAvailabilityTimer(), null, 1000, 1000);
    }

    public void Dispose()
    {
        applyBinding?.Dispose();
        Interlocked.Exchange(ref disposed, 1);
        reconciliationTimer.Dispose();
        mapTimeTracker?.SetObserverClockSuspended(false);
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<CampaignInitializationCompleted>(Handle_CampaignInitializationCompleted);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeAcknowledged>(Handle_FreezeAcknowledged);
        messageBroker.Unsubscribe<NetworkCampaignMovementBaselineApplied>(Handle_MovementBaselineApplied);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        lock (selectionGate)
        {
            lock (peerGate) peerControllerIds.Clear();
        }
    }

    private void Handle_CampaignReady(MessagePayload<CampaignReady> _)
    {
        lock (localReportGate) campaignReady = true;
        ReportLocalAvailability();
    }

    private void Handle_CampaignInitializationCompleted(MessagePayload<CampaignInitializationCompleted> _)
    {
        lock (localReportGate)
        {
            if (campaignInitializationComplete) return;
            campaignInitializationComplete = true;
        }

        if (sessionIdentity != null && sessionIdentity.TryGetConfirmedLease(out var lease))
            Logger.Information("Campaign initialization completed after object registration at authority lease epoch {Epoch}",
                lease.Epoch);
        else
            Logger.Information("Campaign initialization completed after object registration");

        ReconcileCurrentAvailability();
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        lock (localReportGate) mapReady = payload.What.IsMapReady;
        ReportLocalAvailability();
    }

    private void Handle_AvailabilityReport(MessagePayload<NetworkCampaignSimulationAvailabilityReport> payload)
    {
        if (payload.Who is not NetPeer peer ||
            !connections.HasCompletedCampaignSynchronization(peer) ||
            !playerManager.TryGetPlayer(peer, out var player))
        {
            return;
        }

        // A receive callback may have entered before Dispose and resumed after peer-map cleanup.
        if (Volatile.Read(ref disposed) != 0) return;
        var report = payload.What;
        ForgetSupersededPeerReports(peer, player.ControllerId);
        if (!coordinator.Report(player.ControllerId, report.Availability, report.Revision))
        {
            Logger.Warning("Rejected stale campaign availability report from controller {ControllerId} at revision {Revision}",
                player.ControllerId, report.Revision);
            return;
        }

        lock (peerGate) peerControllerIds[peer.Id] = player.ControllerId;
        ReconcileCurrentAvailability();
    }

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (payload.What.PlayerId == null) return;

        string controllerId;
        lock (peerGate)
        {
            if (!peerControllerIds.TryGetValue(payload.What.PlayerId.Id, out controllerId)) return;
            peerControllerIds.Remove(payload.What.PlayerId.Id);
        }

        if (playerManager.TryGetPeer(controllerId, out var currentPeer) && currentPeer != null &&
            currentPeer.Id != payload.What.PlayerId.Id)
        {
            bool currentPeerHasReported;
            lock (peerGate)
            {
                currentPeerHasReported = peerControllerIds.TryGetValue(currentPeer.Id, out var currentControllerId) &&
                    string.Equals(currentControllerId, controllerId, StringComparison.Ordinal);
            }

            // A reconnect may register its NetPeer before the old disconnect event is delivered.
            // Keep its report if it arrived already; otherwise clear the old connection's revision
            // so the new handler can start its per-connection revision stream from one.
            if (currentPeerHasReported) return;
        }

        if (coordinator.Remove(controllerId)) ReconcileCurrentAvailability();
    }

    private void ForgetSupersededPeerReports(NetPeer currentPeer, string controllerId)
    {
        bool removedSupersededPeer = false;
        lock (peerGate)
        {
            var supersededPeers = new List<int>();
            foreach (var entry in peerControllerIds)
            {
                if (entry.Key != currentPeer.Id &&
                    string.Equals(entry.Value, controllerId, StringComparison.Ordinal))
                    supersededPeers.Add(entry.Key);
            }

            foreach (var peer in supersededPeers)
                removedSupersededPeer |= peerControllerIds.Remove(peer);
        }

        if (removedSupersededPeer)
            coordinator.Remove(controllerId);
    }

    private void ReportLocalAvailability()
    {
        ReportLocalAvailability(reconcile: true);
    }

    internal void OnAvailabilityTimer()
    {
        if (Volatile.Read(ref disposed) != 0) return;

        if (ModInformation.IsListenAuthority)
        {
            bool queueHeartbeat;
            lock (localReportGate)
            {
                queueHeartbeat = campaignReady && Interlocked.Exchange(ref heartbeatQueued, 1) == 0;
            }

            if (queueHeartbeat)
            {
                try
                {
                    // Sample local player registration and map state on the game thread. A stalled
                    // game thread stops renewing readiness, allowing the relay to fence this lease.
                    GameThread.EnqueueSafe(() =>
                    {
                        try
                        {
                            if (Volatile.Read(ref disposed) == 0)
                            {
                                ResumeRetainedFreezeAck();
                                RefreshLocalAvailabilityAndReconcile();
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref heartbeatQueued, 0);
                        }
                    }, context: nameof(CampaignSimulationAvailabilityServerHandler));
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref heartbeatQueued, 0);
                    Logger.Warning(ex, "Could not queue local campaign availability heartbeat");
                }
            }
        }

        // Candidate expiry still runs while the game thread is stalled. Lease changes wait behind
        // in-flight game work so its final world mutations can be sent under the old lease.
        ReconcileCurrentAvailability();
    }

    private void ReportLocalAvailability(bool reconcile)
    {
        if (!ModInformation.IsListenAuthority) return;

        bool accepted;
        lock (localReportGate)
        {
            if (!campaignReady) return;

            var controllerId = controllerIdProvider.ControllerId;
            bool localPlayerRegistered = playerManager.TryGetPlayer(controllerId, out _);
            var availability = mapReady && localPlayerRegistered
                ? CampaignSimulationAvailability.MapReady
                : CampaignSimulationAvailability.Unavailable;
            if (localRevision < long.MaxValue) localRevision++;
            accepted = coordinator.Report(controllerId, availability, localRevision);
        }

        if (reconcile && accepted) ReconcileCurrentAvailability();
    }

    internal void RefreshLocalAvailabilityAndReconcile()
    {
        ReportLocalAvailability(reconcile: false);
        ReconcileCurrentAvailability();
    }

    internal void ReconcileCurrentAvailability()
    {
        if (Volatile.Read(ref disposed) != 0) return;

        lock (selectionGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;

            try
            {
                var selection = coordinator.Select();
                ReconcileLease(selection);
                if (hasLoggedSelection &&
                    selection.IsPaused == lastLoggedSelection.IsPaused &&
                    selection.ControllerId == lastLoggedSelection.ControllerId)
                    return;

                hasLoggedSelection = true;
                lastLoggedSelection = selection;
                if (selection.IsPaused)
                {
                    Logger.Information("No player is currently available to simulate the campaign map; candidate set is paused");
                }
                else
                {
                    Logger.Information("Campaign map authority candidate is controller {ControllerId}", selection.ControllerId);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to reconcile campaign authority availability and lease state");
            }
        }
    }

    private void ReconcileLease(CampaignAuthoritySelection selection)
    {
        if (Volatile.Read(ref disposed) != 0 || !ModInformation.IsListenAuthority || network == null || sessionIdentity == null ||
            !IsCampaignInitializationReady()) return;

        if (!GameThread.Instance.IsGameThread)
        {
            if (Interlocked.Exchange(ref leaseReconciliationQueued, 1) != 0) return;
            try
            {
                GameThread.EnqueueSafe(() =>
                {
                    try { ReconcileCurrentAvailability(); }
                    finally { Interlocked.Exchange(ref leaseReconciliationQueued, 0); }
                }, context: nameof(CampaignSimulationAvailabilityServerHandler));
            }
            catch
            {
                Interlocked.Exchange(ref leaseReconciliationQueued, 0);
                throw;
            }
            return;
        }

        lock (leaseGate)
        {
            if (!IsCampaignInitializationReady()) return;
            if (!sessionIdentity.TryGetConfirmedLease(out var currentLease)) return;
            BroadcastPendingLease();
            if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(currentLease, out var applyReason))
            {
                Logger.Warning("Refused campaign lease reconciliation at epoch {Epoch}: {ApplyReason}", currentLease.Epoch, applyReason);
                return;
            }

            string localControllerId = controllerIdProvider.ControllerId;
            string roomHostControllerId = sessionIdentity.RoomHostControllerId;
            if (string.IsNullOrWhiteSpace(roomHostControllerId) ||
                !string.Equals(localControllerId, roomHostControllerId, StringComparison.Ordinal))
                return;

            if (retainedFreezeAck != null && (retainedFreezeAck.Lease != currentLease ||
                retainedFreezeAck.Ack.CandidateControllerId != (selection.IsPaused ? roomHostControllerId : selection.ControllerId)))
            {
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(retainedFreezeAck.Lease,
                    "Selection or lease changed after retaining the final freeze checkpoint.");
                retainedFreezeAck = null;
                return;
            }

            if (currentLease.IsPaused)
            {
                // The host may resume directly because it is the preferred writer and the world
                // is already fenced. A remote candidate must first apply the ordered-stream barrier.
                if (!selection.IsPaused &&
                    string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) &&
                    coordinator.IsFreshMapReady(roomHostControllerId))
                {
                    if (!HasFinalMovementAcknowledgements(currentLease)) return;
                    if (movement != null && (finalMovementCheckpoint == null ||
                        !finalMovementCheckpoint.Precedes(currentLease) || mapTimeTracker == null ||
                        !mapTimeTracker.TryGetCurrentTicks(out long finalTicks) || finalTicks != finalMovementCheckpoint.Ticks ||
                        !movement.IsInstalled(finalMovementCheckpoint))) return;
                    ResetPendingBarrier();
                    TryCommitLease(currentLease, roomHostControllerId, isPaused: false);
                    return;
                }

                if (!selection.IsPaused &&
                    !string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) &&
                    coordinator.IsFreshMapReady(selection.ControllerId))
                {
                    TrySendHandoffBarrier(currentLease, selection.ControllerId);
                    return;
                }

                ResetPendingBarrier();
                return;
            }

            if (!string.Equals(currentLease.AuthorityControllerId, localControllerId, StringComparison.Ordinal))
            {
                if (!selection.IsPaused &&
                    string.Equals(selection.ControllerId, currentLease.AuthorityControllerId, StringComparison.Ordinal))
                {
                    CancelPendingFreeze(currentLease);
                    return;
                }

                // No readiness report, disconnect or timeout can substitute for the old writer's
                // final checkpoint. Even an all-away pause waits for its freeze acknowledgement.
                TrySendFreezeRequest(currentLease, selection.IsPaused ? roomHostControllerId : selection.ControllerId);
                return;
            }

            if (!selection.IsPaused &&
                coordinator.IsFreshMapReady(currentLease.AuthorityControllerId))
                return;

            // Finish the current game-thread tick and send its buffered results while the old lease
            // can still wrap them. Freezing first would turn them into rejected unscoped writes.
            // Native worker callbacks must remain able to acquire the lease while they finish.
            if (asyncTasks == null || !asyncTasks.TryDrain()) return;
            bool committedPause = false;
            if (!sessionIdentity.TryExecuteWithLease(currentLease, () =>
            {
                if (movementPreparation != null && !movementPreparation.TryPrepareForFreeze(currentLease)) return;
                sendCoalescer?.Flush(network);
                network.FlushPendingMessages();
                if (movement != null)
                {
                    if (mapTimeTracker == null || !mapTimeTracker.TryGetCurrentTicks(out long frozenTicks) ||
                        !movement.TryCapture(currentLease, frozenTicks, out finalMovementCheckpoint)) return;
                }
                committedPause = TryCommitLease(currentLease, string.Empty, isPaused: true);
            }) || !committedPause) return;

            if (!selection.IsPaused &&
                !string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) &&
                coordinator.IsFreshMapReady(selection.ControllerId) &&
                sessionIdentity.TryGetConfirmedLease(out var pausedLease))
                TrySendHandoffBarrier(pausedLease, selection.ControllerId);
        }
    }

    private bool IsCampaignInitializationReady()
    {
        lock (localReportGate) return campaignReady && campaignInitializationComplete;
    }

    private bool TryCommitLease(AuthorityLease currentLease, string authorityControllerId, bool isPaused)
    {
        if (currentLease.Epoch == long.MaxValue)
        {
            Logger.Error("Cannot advance campaign authority lease beyond epoch {Epoch}; campaign remains fenced",
                currentLease.Epoch);
            return false;
        }

        var nextLease = new AuthorityLease(
            currentLease.RoomId,
            Guid.NewGuid().ToString("N"),
            authorityControllerId,
            currentLease.Epoch + 1,
            isPaused);
        string roomHostControllerId = sessionIdentity.RoomHostControllerId;
        if (!sessionIdentity.TryApply(nextLease, roomHostControllerId))
        {
            Logger.Warning("Could not commit campaign authority lease epoch {Epoch} for controller {ControllerId}",
                nextLease.Epoch, authorityControllerId);
            return false;
        }

        if (!isPaused && movement != null)
            Logger.Information("Activated campaign authority epoch {Epoch} controller {ControllerId} final movement source epoch {SourceEpoch} ticks {Ticks} sha256 {Sha256} outstanding member receipts {Count}",
                nextLease.Epoch, authorityControllerId, finalMovementCheckpoint?.Epoch, finalMovementCheckpoint?.Ticks,
                finalMovementCheckpoint?.Sha256, pendingMovementAcks.Count);
        mapTimeTracker?.SetObserverClockSuspended(false);
        if (nextLease.IsPaused && movement != null) BeginMovementAcknowledgements(nextLease);
        pendingLeaseBroadcast = nextLease;
        BroadcastPendingLease();
        Logger.Information("Committed {LeaseState} campaign authority lease epoch {Epoch} for controller {ControllerId}",
            isPaused ? "paused" : "active", nextLease.Epoch, authorityControllerId);
        return true;
    }

    private void TrySendHandoffBarrier(AuthorityLease pausedLease, string candidateControllerId)
    {
        if (objectManager == null || rosterCheckpointService == null || mapTimeTracker == null ||
            sessionCheckpointService == null || schedulerCheckpointService == null || !HasFinalMovementAcknowledgements(pausedLease)) return;

        if (!pausedLease.IsPaused || string.IsNullOrWhiteSpace(candidateControllerId) ||
            string.Equals(candidateControllerId, sessionIdentity.RoomHostControllerId, StringComparison.Ordinal) ||
            network == null || !coordinator.IsFreshMapReady(candidateControllerId))
            return;

        if (!sessionIdentity.TryGetConfirmedLease(out var currentLease) || currentLease != pausedLease)
            return;

        NetPeer candidatePeer = null;
        if (!playerManager.TryGetPeer(candidateControllerId, out candidatePeer) || candidatePeer == null)
            return;

        lock (peerGate)
        {
            if (!peerControllerIds.TryGetValue(candidatePeer.Id, out var mappedControllerId) ||
                !string.Equals(mappedControllerId, candidateControllerId, StringComparison.Ordinal))
                return;
        }

        if (!connections.HasCompletedCampaignSynchronization(candidatePeer))
            return;

        if (hasPendingBarrier && pendingBarrierEpoch == pausedLease.Epoch &&
            string.Equals(pendingBarrierAuthorityId, pausedLease.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(pendingBarrierCandidateControllerId, candidateControllerId, StringComparison.Ordinal) &&
            ReferenceEquals(pendingBarrierCandidatePeer, candidatePeer))
            return;

        if (!rosterCheckpointService.TryCapture(out var rosterCheckpoint))
        {
            Logger.Warning("Could not capture world rosters for controller {ControllerId}; campaign remains paused without a handoff barrier",
                candidateControllerId);
            return;
        }

        if (!mapTimeTracker.TryGetCurrentTicks(out long ticks) || ticks < 0)
        {
            Logger.Warning("Could not capture final clock for controller {ControllerId}; campaign remains paused without a handoff barrier",
                candidateControllerId);
            return;
        }

        if (!sessionCheckpointService.TryCapture(out var sessionCheckpoint))
        {
            Logger.Warning("Could not capture co-op session for controller {ControllerId}; campaign remains paused without a handoff barrier",
                candidateControllerId);
            return;
        }

        if (!TryGetNextBarrierSequence(out var sequence))
        {
            Logger.Error("Cannot issue another campaign handoff barrier; sequence space is exhausted");
            return;
        }

        if (!schedulerCheckpointService.TryCapture(out var schedulerCheckpoint))
        {
            Logger.Warning("Could not capture campaign scheduler for controller {ControllerId}; campaign remains paused",
                candidateControllerId);
            return;
        }
        if (movement != null && (finalMovementCheckpoint == null || !finalMovementCheckpoint.Precedes(pausedLease) ||
            finalMovementCheckpoint.Ticks != ticks || !movement.IsInstalled(finalMovementCheckpoint))) return;

        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            pausedLease.RoomId,
            pausedLease.AuthorityId,
            pausedLease.Epoch,
            candidateControllerId,
            sequence,
            objectManager.CaptureAllocationCheckpoint(),
            rosterCheckpoint,
            new CampaignClockCheckpoint(ticks),
            sessionCheckpoint,
            schedulerCheckpoint,
            finalMovementCheckpoint);

        try
        {
            // The paused lease and all earlier reliable-ordered world messages must be queued before
            // the barrier. Its acknowledgement is emitted only after the candidate's game-thread
            // apply queue has drained those messages.
            BroadcastPendingLease();
            if (pendingLeaseBroadcast.HasValue) return;
            network.FlushPendingMessages();
            network.Send(candidatePeer, barrier);
            network.FlushPendingMessages();
            pendingBarrierSequence = sequence;
            pendingBarrierEpoch = pausedLease.Epoch;
            pendingBarrierAuthorityId = pausedLease.AuthorityId;
            pendingBarrierCandidateControllerId = candidateControllerId;
            pendingBarrierCandidatePeer = candidatePeer;
            pendingBarrierAcknowledged = false;
            hasPendingBarrier = true;
            Logger.Information("Sent campaign authority handoff barrier {BarrierSequence} for paused epoch {Epoch} to controller {ControllerId}",
                sequence, pausedLease.Epoch, candidateControllerId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to send campaign authority handoff barrier {BarrierSequence} to controller {ControllerId}",
                sequence, candidateControllerId);
        }
    }

    private void Handle_HandoffBarrierApplied(MessagePayload<NetworkCampaignAuthorityHandoffBarrierApplied> payload)
    {
        if (payload.Who is not NetPeer peer) return;
        GameThread.EnqueueSafe(() => ApplyHandoffBarrierAcknowledgement(peer, payload.What),
            context: nameof(CampaignSimulationAvailabilityServerHandler));
    }

    private void ApplyHandoffBarrierAcknowledgement(NetPeer peer, NetworkCampaignAuthorityHandoffBarrierApplied ack)
    {
        if (!ModInformation.IsListenAuthority || network == null || sessionIdentity == null ||
            !connections.HasCompletedCampaignSynchronization(peer) ||
            !playerManager.TryGetPlayer(peer, out var player))
            return;

        lock (selectionGate)
        {
            lock (leaseGate)
            {
                if (!hasPendingBarrier || pendingBarrierAcknowledged ||
                    !ReferenceEquals(peer, pendingBarrierCandidatePeer) ||
                    !sessionIdentity.TryGetConfirmedLease(out var currentLease) || !currentLease.IsPaused ||
                    currentLease.Epoch != pendingBarrierEpoch ||
                    !coordinator.IsFreshMapReady(player.ControllerId))
                    return;

                if (ack.BarrierSequence != pendingBarrierSequence ||
                    ack.PausedEpoch != pendingBarrierEpoch ||
                    !string.Equals(ack.RoomId, currentLease.RoomId, StringComparison.Ordinal) ||
                    !string.Equals(ack.PausedAuthorityId, pendingBarrierAuthorityId, StringComparison.Ordinal) ||
                    !string.Equals(ack.CandidateControllerId, pendingBarrierCandidateControllerId, StringComparison.Ordinal) ||
                    !string.Equals(player.ControllerId, pendingBarrierCandidateControllerId, StringComparison.Ordinal))
                    return;

                lock (peerGate)
                {
                    if (!peerControllerIds.TryGetValue(peer.Id, out var knownControllerId) ||
                        !string.Equals(knownControllerId, player.ControllerId, StringComparison.Ordinal))
                        return;
                }
                if (!playerManager.TryGetPeer(player.ControllerId, out var currentPeer) || currentPeer != peer) return;

                var selection = coordinator.Select();
                if (selection.IsPaused ||
                    !string.Equals(selection.ControllerId, player.ControllerId, StringComparison.Ordinal))
                    return;

                if (!TryCommitLease(currentLease, player.ControllerId, isPaused: false)) return;
                pendingBarrierAcknowledged = true;
                Logger.Information("Activated controller {ControllerId} after campaign handoff barrier {BarrierSequence} at epoch {Epoch}",
                    player.ControllerId, pendingBarrierSequence, currentLease.Epoch + 1);
                ResetPendingBarrier();
            }
        }
    }

    private void TrySendFreezeRequest(AuthorityLease currentLease, string candidateControllerId)
    {
        if (string.IsNullOrWhiteSpace(candidateControllerId) ||
            string.Equals(currentLease.AuthorityControllerId, candidateControllerId, StringComparison.Ordinal)) return;
        if (!hasPendingFreeze || pendingFreezeLease != currentLease ||
            !string.Equals(pendingFreezeCandidateControllerId, candidateControllerId, StringComparison.Ordinal))
        {
            CancelPendingFreeze(currentLease);
            if (!TryGetNextBarrierSequence(out var sequence)) return;
            pendingFreezeLease = currentLease;
            pendingFreezeCandidateControllerId = candidateControllerId;
            pendingFreezeSequence = sequence;
            hasPendingFreeze = true;
        }

        mapTimeTracker?.SetObserverClockSuspended(true);
        if (!TryGetSynchronizedPeer(currentLease.AuthorityControllerId, out var peer)) return;
        network.Send(peer, new NetworkCampaignAuthorityFreezeRequest(currentLease,
            pendingFreezeCandidateControllerId, pendingFreezeSequence));
        network.FlushPendingMessages();
    }

    private bool TryGetSynchronizedPeer(string controllerId, out NetPeer peer)
    {
        if (!playerManager.TryGetPeer(controllerId, out peer) || peer == null ||
            !connections.HasCompletedCampaignSynchronization(peer)) return false;
        lock (peerGate)
            return peerControllerIds.TryGetValue(peer.Id, out var mapped) &&
                string.Equals(mapped, controllerId, StringComparison.Ordinal);
    }

    private void CancelPendingFreeze(AuthorityLease currentLease)
    {
        if (!hasPendingFreeze) return;
        if (pendingFreezeLease == currentLease && TryGetSynchronizedPeer(currentLease.AuthorityControllerId, out var peer))
        {
            network.Send(peer, new NetworkCampaignAuthorityFreezeRequest(pendingFreezeLease,
                pendingFreezeCandidateControllerId, pendingFreezeSequence).ToCanceled());
            network.FlushPendingMessages();
        }
        hasPendingFreeze = false;
        retainedFreezeAck = null;
        pendingFreezeLease = default;
        pendingFreezeCandidateControllerId = string.Empty;
        pendingFreezeSequence = 0;
        mapTimeTracker?.SetObserverClockSuspended(false);
    }

    private void Handle_FreezeAcknowledged(MessagePayload<NetworkCampaignAuthorityFreezeAcknowledged> payload)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (payload.Who is not NetPeer peer)
        {
            LogFreezeAckStage("ingress-rejected-non-peer", null, payload.What);
            return;
        }
        LogFreezeAckStage("ingress", peer, payload.What);
        GameThread.EnqueueSafe(() =>
        {
            try { ApplyFreezeAcknowledgement(peer, payload.What); }
            catch (Exception ex)
            {
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(sessionIdentity.CurrentLease,
                    "Critical freeze checkpoint installation threw: " + ex);
                throw;
            }
        },
            context: nameof(CampaignSimulationAvailabilityServerHandler));
    }

    private void ApplyFreezeAcknowledgement(NetPeer peer, NetworkCampaignAuthorityFreezeAcknowledged ack)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        LogFreezeAckStage("game-thread", peer, ack);
        Player player = null;
        if (RejectFreezeAck(!ModInformation.IsListenAuthority, "session-mode", peer, ack) ||
            RejectFreezeAck(network == null, "network-unavailable", peer, ack) ||
            RejectFreezeAck(sessionIdentity == null, "identity-unavailable", peer, ack) ||
            RejectFreezeAck(!connections.HasCompletedCampaignSynchronization(peer), "peer-not-synchronized", peer, ack) ||
            RejectFreezeAck(!playerManager.TryGetPlayer(peer, out player), "peer-player-unmapped", peer, ack)) return;
        lock (selectionGate)
        lock (leaseGate)
        {
            AuthorityLease lease = default;
            NetPeer currentPeer = null;
            if (RejectFreezeAck(!hasPendingFreeze, "no-pending-freeze", peer, ack) ||
                RejectFreezeAck(!sessionIdentity.TryGetConfirmedLease(out lease), "lease-unconfirmed", peer, ack) ||
                RejectFreezeAck(lease != pendingFreezeLease, "pending-lease-mismatch", peer, ack) ||
                RejectFreezeAck(ack.HandoffSequence != pendingFreezeSequence, "sequence-mismatch", peer, ack) ||
                RejectFreezeAck(ack.Epoch != lease.Epoch, "epoch-mismatch", peer, ack) ||
                RejectFreezeAck(!string.Equals(ack.RoomId, lease.RoomId, StringComparison.Ordinal), "room-mismatch", peer, ack) ||
                RejectFreezeAck(!string.Equals(ack.AuthorityId, lease.AuthorityId, StringComparison.Ordinal), "authority-mismatch", peer, ack) ||
                RejectFreezeAck(!string.Equals(ack.AuthorityControllerId, lease.AuthorityControllerId, StringComparison.Ordinal), "writer-mismatch", peer, ack) ||
                RejectFreezeAck(!string.Equals(ack.CandidateControllerId, pendingFreezeCandidateControllerId, StringComparison.Ordinal), "candidate-mismatch", peer, ack) ||
                RejectFreezeAck(!string.Equals(player.ControllerId, lease.AuthorityControllerId, StringComparison.Ordinal), "player-writer-mismatch", peer, ack) ||
                RejectFreezeAck(!TryGetSynchronizedPeer(player.ControllerId, out currentPeer), "current-peer-unavailable", peer, ack) ||
                RejectFreezeAck(currentPeer != peer, "current-peer-mismatch", peer, ack)) return;
            LogFreezeAckStage("qualified-current-peer", peer, ack);

            var selection = coordinator.Select();
            string candidate = selection.IsPaused ? sessionIdentity.RoomHostControllerId : selection.ControllerId;
            if (!string.Equals(candidate, pendingFreezeCandidateControllerId, StringComparison.Ordinal))
            {
                LogFreezeAckStage("selection-changed-cancel", peer, ack);
                if (retainedFreezeAck != null)
                {
                    CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Candidate changed after a final freeze ACK was retained.");
                    retainedFreezeAck = null;
                    return;
                }
                CancelPendingFreeze(lease);
                return;
            }

            // The ACK is transport-authenticated above, but receipt completion may span later frames.
            // Retain one complete immutable request rather than requiring the old writer to send it again.
            bool completeAck = ack.AllocationCheckpoint != null && ack.RosterCheckpoint != null &&
                ack.SessionCheckpoint != null && ack.SchedulerCheckpoint != null && ack.ClockCheckpoint?.Ticks >= 0 &&
                (movement == null || ack.MovementCheckpoint != null);
            if (!completeAck && retainedFreezeAck != null)
            {
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Incomplete redelivery conflicts with a retained final freeze checkpoint.");
                return;
            }
            if (completeAck)
            {
                string fingerprint = FreezeAckFingerprint(ack);
                if (retainedFreezeAck != null && (retainedFreezeAck.Peer != peer || retainedFreezeAck.Lease != lease ||
                    retainedFreezeAck.Ack.HandoffSequence != ack.HandoffSequence || retainedFreezeAck.Fingerprint != fingerprint))
                {
                    CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Conflicting authenticated final freeze checkpoint.");
                    return;
                }
                retainedFreezeAck ??= new VerifiedFreezeAck { Peer = peer, Ack = ack, Lease = lease, Fingerprint = fingerprint };
            }
            var applyStatus = CampaignApplyCompletion.For(sessionIdentity).GetStatusThrough(lease, out _);
            if (applyStatus != CampaignApplyStatus.Ready)
            {
                RejectFreezeAck(true, applyStatus == CampaignApplyStatus.Pending ? "critical-apply-pending-retained" : "critical-apply-faulted", peer, ack);
                return;
            }
            if (RejectFreezeAck(asyncTasks == null, "async-drain-unavailable", peer, ack) ||
                RejectFreezeAck(!asyncTasks.TryDrain(), "async-drain-incomplete", peer, ack)) return;
            Func<bool> installMovement = null;
            if (movement != null && (RejectFreezeAck(ack.MovementCheckpoint == null, "movement-missing", peer, ack) ||
                RejectFreezeAck(ack.ClockCheckpoint == null, "movement-clock-missing", peer, ack) ||
                RejectFreezeAck(!movement.TryPrepare(ack.MovementCheckpoint, lease, ack.ClockCheckpoint.Ticks, out installMovement), "movement-prepare", peer, ack))) return;
            if (retainedFreezeAck == null || !retainedFreezeAck.Installed)
            {
                using (CampaignApplyCompletion.For(sessionIdentity).ObserveSource(typeof(NetworkCampaignAuthorityFreezeAcknowledged), lease))
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
                {
                    if (RejectFreezeAck(ack.AllocationCheckpoint == null, "allocation-missing", peer, ack) ||
                        RejectFreezeAck(ack.RosterCheckpoint == null, "roster-missing", peer, ack) ||
                        RejectFreezeAck(ack.SessionCheckpoint == null, "session-missing", peer, ack) ||
                        RejectFreezeAck(ack.SchedulerCheckpoint == null, "scheduler-missing", peer, ack) ||
                        RejectFreezeAck(ack.ClockCheckpoint == null, "clock-missing", peer, ack) ||
                        RejectFreezeAck(ack.ClockCheckpoint.Ticks < 0, "clock-negative", peer, ack) ||
                        RejectFreezeAck(objectManager == null, "allocation-service-unavailable", peer, ack) ||
                        RejectFreezeAck(!objectManager.TryApplyAllocationCheckpoint(ack.AllocationCheckpoint), "allocation-apply", peer, ack) ||
                        RejectFreezeAck(rosterCheckpointService == null, "roster-service-unavailable", peer, ack) ||
                        RejectFreezeAck(!rosterCheckpointService.TryApply(ack.RosterCheckpoint), "roster-apply", peer, ack) ||
                        RejectFreezeAck(sessionCheckpointService == null, "session-service-unavailable", peer, ack) ||
                        RejectFreezeAck(!sessionCheckpointService.TryApply(ack.SessionCheckpoint), "session-apply", peer, ack) ||
                        RejectFreezeAck(schedulerCheckpointService == null, "scheduler-service-unavailable", peer, ack) ||
                        RejectFreezeAck(!schedulerCheckpointService.TryApply(ack.SchedulerCheckpoint), "scheduler-apply", peer, ack) ||
                        RejectFreezeAck(mapTimeTracker == null, "clock-service-unavailable", peer, ack) ||
                        RejectFreezeAck(!mapTimeTracker.TryApplyAuthorityHandoffBaseline(ack.ClockCheckpoint.Ticks), "clock-apply", peer, ack))
                    {
                        Logger.Error("Rejected frozen campaign checkpoint {HandoffSequence} from {ControllerId}; no replacement authority was activated",
                            ack.HandoffSequence, player.ControllerId);
                        return;
                    }

                    if (installMovement != null && RejectFreezeAck(!installMovement(), "movement-install", peer, ack)) return;
                }
                if (retainedFreezeAck != null) retainedFreezeAck.Installed = true;
            }
            if (RejectFreezeAck(!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out _),
                "checkpoint-descendants-incomplete-or-faulted", peer, ack)) return;
            if (Volatile.Read(ref disposed) != 0) return;
            finalMovementCheckpoint = ack.MovementCheckpoint;
            if (RejectFreezeAck(!TryCommitLease(lease, string.Empty, isPaused: true), "paused-lease-commit", peer, ack)) return;
            hasPendingFreeze = false;
            retainedFreezeAck = null;
            ResetPendingBarrier();
            Logger.Information("Applied final frozen campaign checkpoint {HandoffSequence} from {ControllerId}; reconciling the next writer",
                ack.HandoffSequence, player.ControllerId);
            ReconcileLease(coordinator.Select());
        }
    }

    private bool RejectFreezeAck(bool rejected, string stage, NetPeer peer,
        NetworkCampaignAuthorityFreezeAcknowledged ack)
    {
        if (rejected)
        {
            LogFreezeAckStage("rejected-" + stage, peer, ack);
            if (stage.EndsWith("-apply", StringComparison.Ordinal) || stage == "movement-install")
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(sessionIdentity.CurrentLease,
                    "Critical freeze checkpoint installation refused at " + stage);
        }
        return rejected;
    }

    private static void LogFreezeAckStage(string stage, NetPeer peer, NetworkCampaignAuthorityFreezeAcknowledged ack)
    {
        Logger.Information("Freeze ACK receive stage {Stage} handoff {HandoffSequence} room {RoomId} authority {AuthorityId} writer {WriterControllerId} epoch {Epoch} candidate {CandidateControllerId} peerId {PeerId} peerRef {PeerReference} endpoint {Endpoint} state {PeerState}",
            stage, ack.HandoffSequence, ack.RoomId, ack.AuthorityId, ack.AuthorityControllerId, ack.Epoch,
            ack.CandidateControllerId, peer?.Id, peer == null ? 0 : RuntimeHelpers.GetHashCode(peer), peer?.ToString(), peer?.ConnectionState);
    }

    private void ResetPendingBarrier()
    {
        hasPendingBarrier = false;
        pendingBarrierAcknowledged = false;
        pendingBarrierSequence = 0;
        pendingBarrierEpoch = 0;
        pendingBarrierAuthorityId = string.Empty;
        pendingBarrierCandidateControllerId = string.Empty;
        pendingBarrierCandidatePeer = null;
    }

    private static bool TryGetNextBarrierSequence(out long sequence)
    {
        while (true)
        {
            long current = Interlocked.Read(ref nextBarrierSequence);
            if (current == long.MaxValue)
            {
                sequence = 0;
                return false;
            }

            long next = current + 1;
            if (Interlocked.CompareExchange(ref nextBarrierSequence, next, current) == current)
            {
                sequence = next;
                return true;
            }
        }
    }

    private void BeginMovementAcknowledgements(AuthorityLease paused)
    {
        pendingMovementLease = paused;
        pendingMovementAcks.Clear();
        lock (peerGate)
        {
            foreach (var entry in peerControllerIds)
            {
                if (playerManager.TryGetPeer(entry.Value, out var peer) && peer != null && peer.Id == entry.Key &&
                    connections.HasCompletedCampaignSynchronization(peer))
                    pendingMovementAcks.Add(peer.Id, (peer, entry.Value));
            }
        }
        Logger.Information("Final movement paused epoch {Epoch} awaiting {Count} synchronized member acknowledgements sha256 {Sha256}",
            paused.Epoch, pendingMovementAcks.Count, finalMovementCheckpoint?.Sha256);
    }

    private bool HasFinalMovementAcknowledgements(AuthorityLease paused) => movement == null ||
        (pendingMovementLease == paused && pendingMovementAcks.Count == 0 && finalMovementCheckpoint != null);

    private void Handle_MovementBaselineApplied(MessagePayload<NetworkCampaignMovementBaselineApplied> payload)
    {
        if (payload.Who is not NetPeer peer)
        {
            LogMovementAckStage("ingress-rejected-non-peer", null, payload.What, payload.Who?.GetType().FullName);
            return;
        }
        var ack = payload.What;
        LogMovementAckStage("ingress", peer, ack, payload.Who.GetType().FullName);
        GameThread.EnqueueSafe(() => ApplyMovementAcknowledgement(peer, ack), context: nameof(CampaignSimulationAvailabilityServerHandler));
        LogMovementAckStage("queued", peer, ack, payload.Who.GetType().FullName);
    }

    private void ApplyMovementAcknowledgement(NetPeer peer, NetworkCampaignMovementBaselineApplied ack)
    {
        LogMovementAckStage("game-thread", peer, ack);
        lock (selectionGate)
        lock (leaseGate)
        {
            AuthorityLease paused = default;
            (NetPeer peer, string controller) expected = default;
            NetPeer currentPeer = null;
            bool Reject(bool rejected, string reason)
            {
                if (rejected) LogMovementAckState("rejected-" + reason, peer, ack, paused, expected.peer, expected.controller, currentPeer);
                return rejected;
            }
            if (Reject(movement == null, "movement-unavailable") ||
                Reject(!sessionIdentity.TryGetConfirmedLease(out paused), "lease-unconfirmed") ||
                Reject(!paused.IsPaused, "lease-not-paused") ||
                Reject(pendingMovementLease != paused, "pending-lease-mismatch") ||
                Reject(finalMovementCheckpoint == null, "checkpoint-missing") ||
                Reject(!pendingMovementAcks.TryGetValue(peer.Id, out expected), "member-not-pending") ||
                Reject(!ReferenceEquals(expected.peer, peer), "pending-peer-reference") ||
                Reject(ack.RoomId != paused.RoomId, "room-mismatch") ||
                Reject(ack.PausedAuthorityId != paused.AuthorityId, "authority-mismatch") ||
                Reject(ack.PausedEpoch != paused.Epoch, "epoch-mismatch") ||
                Reject(ack.Ticks != finalMovementCheckpoint.Ticks, "ticks-mismatch") ||
                Reject(ack.Sha256 != finalMovementCheckpoint.Sha256, "hash-mismatch") ||
                Reject(ack.ControllerId != expected.controller, "controller-mismatch") ||
                Reject(!TryGetSynchronizedPeer(ack.ControllerId, out currentPeer), "current-synchronized-peer-unavailable") ||
                Reject(!ReferenceEquals(peer, currentPeer), "current-peer-reference")) return;
            LogMovementAckState("accepted", peer, ack, paused, expected.peer, expected.controller, currentPeer);
            pendingMovementAcks.Remove(peer.Id);
            Logger.Information("Confirmed final movement member {ControllerId} paused epoch {Epoch} remaining {Count} ticks {Ticks} sha256 {Sha256}",
                ack.ControllerId, paused.Epoch, pendingMovementAcks.Count, ack.Ticks, ack.Sha256);
            ReconcileLease(coordinator.Select());
        }
    }

    private void LogMovementAckStage(string stage, NetPeer peer, NetworkCampaignMovementBaselineApplied ack, string whoType = null)
    {
        Logger.Information("Movement baseline ACK server stage {Stage} room {RoomId} authority {AuthorityId} epoch {Epoch} controller {ControllerId} ticks {Ticks} sha256 {Sha256} peerId {PeerId} peerRef {PeerReference} endpoint {Endpoint} state {PeerState} whoType {WhoType} thread {ThreadId} handlerRef {HandlerReference} networkRef {NetworkReference}",
            stage, ack.RoomId, ack.PausedAuthorityId, ack.PausedEpoch, ack.ControllerId, ack.Ticks, ack.Sha256,
            peer?.Id, peer == null ? 0 : RuntimeHelpers.GetHashCode(peer), peer?.ToString(), peer?.ConnectionState,
            whoType, Thread.CurrentThread.ManagedThreadId, RuntimeHelpers.GetHashCode(this), network == null ? 0 : RuntimeHelpers.GetHashCode(network));
    }

    private void LogMovementAckState(string stage, NetPeer peer, NetworkCampaignMovementBaselineApplied ack,
        AuthorityLease current, NetPeer expectedPeer, string expectedController, NetPeer currentPeer)
    {
        LogMovementAckStage(stage, peer, ack);
        Logger.Information("Movement baseline ACK guard state {Stage} currentRoom {CurrentRoomId} currentAuthority {CurrentAuthorityId} currentEpoch {CurrentEpoch} currentPaused {CurrentPaused} pendingRoom {PendingRoomId} pendingAuthority {PendingAuthorityId} pendingEpoch {PendingEpoch} expectedController {ExpectedControllerId} expectedPeerRef {ExpectedPeerReference} currentPeerRef {CurrentPeerReference} expectedTicks {ExpectedTicks} expectedSha256 {ExpectedSha256} remaining {Count} handlerRef {HandlerReference}",
            stage, current.RoomId, current.AuthorityId, current.Epoch, current.IsPaused,
            pendingMovementLease.RoomId, pendingMovementLease.AuthorityId, pendingMovementLease.Epoch, expectedController,
            expectedPeer == null ? 0 : RuntimeHelpers.GetHashCode(expectedPeer), currentPeer == null ? 0 : RuntimeHelpers.GetHashCode(currentPeer),
            finalMovementCheckpoint?.Ticks, finalMovementCheckpoint?.Sha256, pendingMovementAcks.Count, RuntimeHelpers.GetHashCode(this));
    }

    private void BroadcastPendingLease()
    {
        if (!pendingLeaseBroadcast.HasValue) return;

        var lease = pendingLeaseBroadcast.Value;
        try
        {
            network.SendAll(new NetworkAuthorityLeaseSnapshot(lease, sessionIdentity.RoomHostControllerId,
                lease.IsPaused ? finalMovementCheckpoint : null));
            pendingLeaseBroadcast = null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to distribute campaign authority lease epoch {Epoch}; will retry",
                lease.Epoch);
        }
    }
}
