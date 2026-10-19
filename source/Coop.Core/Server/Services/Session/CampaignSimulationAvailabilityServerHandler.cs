using Common;
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
using Coop.Core.Server.States.Messages;

namespace Coop.Core.Server.Services.Session;

public sealed class CampaignSimulationAvailabilityServerHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSimulationAvailabilityServerHandler>();
    private static long nextBarrierSequence;

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
        ICampaignAsyncTaskDrain asyncTasks = null)
    {
        this.messageBroker = messageBroker;
        this.coordinator = coordinator;
        this.connections = connections;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        this.objectManager = objectManager;
        this.sendCoalescer = sendCoalescer;
        this.rosterCheckpointService = rosterCheckpointService;
        this.mapTimeTracker = mapTimeTracker;
        this.sessionCheckpointService = sessionCheckpointService;
        this.schedulerCheckpointService = schedulerCheckpointService;
        this.asyncTasks = asyncTasks;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<CampaignInitializationCompleted>(Handle_CampaignInitializationCompleted);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeAcknowledged>(Handle_FreezeAcknowledged);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        reconciliationTimer = new Timer(_ => OnAvailabilityTimer(), null, 1000, 1000);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        reconciliationTimer.Dispose();
        mapTimeTracker?.SetObserverClockSuspended(false);
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<CampaignInitializationCompleted>(Handle_CampaignInitializationCompleted);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeAcknowledged>(Handle_FreezeAcknowledged);
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
                            if (Volatile.Read(ref disposed) == 0) RefreshLocalAvailabilityAndReconcile();
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
        if (!ModInformation.IsListenAuthority || network == null || sessionIdentity == null ||
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

            string localControllerId = controllerIdProvider.ControllerId;
            string roomHostControllerId = sessionIdentity.RoomHostControllerId;
            if (string.IsNullOrWhiteSpace(roomHostControllerId) ||
                !string.Equals(localControllerId, roomHostControllerId, StringComparison.Ordinal))
                return;

            if (currentLease.IsPaused)
            {
                // The host may resume directly because it is the preferred writer and the world
                // is already fenced. A remote candidate must first apply the ordered-stream barrier.
                if (!selection.IsPaused &&
                    string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) &&
                    coordinator.IsFreshMapReady(roomHostControllerId))
                {
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
                sendCoalescer?.Flush(network);
                network.FlushPendingMessages();
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

        mapTimeTracker?.SetObserverClockSuspended(false);
        pendingLeaseBroadcast = nextLease;
        BroadcastPendingLease();
        Logger.Information("Committed {LeaseState} campaign authority lease epoch {Epoch} for controller {ControllerId}",
            isPaused ? "paused" : "active", nextLease.Epoch, authorityControllerId);
        return true;
    }

    private void TrySendHandoffBarrier(AuthorityLease pausedLease, string candidateControllerId)
    {
        if (objectManager == null || rosterCheckpointService == null || mapTimeTracker == null ||
            sessionCheckpointService == null || schedulerCheckpointService == null) return;

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
            schedulerCheckpoint);

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
        pendingFreezeLease = default;
        pendingFreezeCandidateControllerId = string.Empty;
        pendingFreezeSequence = 0;
        mapTimeTracker?.SetObserverClockSuspended(false);
    }

    private void Handle_FreezeAcknowledged(MessagePayload<NetworkCampaignAuthorityFreezeAcknowledged> payload)
    {
        if (payload.Who is not NetPeer peer) return;
        GameThread.EnqueueSafe(() => ApplyFreezeAcknowledgement(peer, payload.What),
            context: nameof(CampaignSimulationAvailabilityServerHandler));
    }

    private void ApplyFreezeAcknowledgement(NetPeer peer, NetworkCampaignAuthorityFreezeAcknowledged ack)
    {
        if (!ModInformation.IsListenAuthority || network == null || sessionIdentity == null ||
            !connections.HasCompletedCampaignSynchronization(peer) || !playerManager.TryGetPlayer(peer, out var player)) return;
        lock (selectionGate)
        lock (leaseGate)
        {
            if (!hasPendingFreeze || !sessionIdentity.TryGetConfirmedLease(out var lease) || lease != pendingFreezeLease ||
                ack.HandoffSequence != pendingFreezeSequence || ack.Epoch != lease.Epoch ||
                !string.Equals(ack.RoomId, lease.RoomId, StringComparison.Ordinal) ||
                !string.Equals(ack.AuthorityId, lease.AuthorityId, StringComparison.Ordinal) ||
                !string.Equals(ack.AuthorityControllerId, lease.AuthorityControllerId, StringComparison.Ordinal) ||
                !string.Equals(ack.CandidateControllerId, pendingFreezeCandidateControllerId, StringComparison.Ordinal) ||
                !string.Equals(player.ControllerId, lease.AuthorityControllerId, StringComparison.Ordinal) ||
                !TryGetSynchronizedPeer(player.ControllerId, out var currentPeer) || currentPeer != peer) return;

            var selection = coordinator.Select();
            string candidate = selection.IsPaused ? sessionIdentity.RoomHostControllerId : selection.ControllerId;
            if (!string.Equals(candidate, pendingFreezeCandidateControllerId, StringComparison.Ordinal))
            {
                CancelPendingFreeze(lease);
                return;
            }

            // Earlier remote world deltas have already applied in this same game-thread FIFO.
            if (ack.AllocationCheckpoint == null || ack.RosterCheckpoint == null || ack.SessionCheckpoint == null ||
                ack.SchedulerCheckpoint == null || asyncTasks == null || !asyncTasks.TryDrain() ||
                ack.ClockCheckpoint == null || ack.ClockCheckpoint.Ticks < 0 ||
                objectManager == null || !objectManager.TryApplyAllocationCheckpoint(ack.AllocationCheckpoint) ||
                rosterCheckpointService == null || !rosterCheckpointService.TryApply(ack.RosterCheckpoint) ||
                sessionCheckpointService == null || !sessionCheckpointService.TryApply(ack.SessionCheckpoint) ||
                schedulerCheckpointService == null || !schedulerCheckpointService.TryApply(ack.SchedulerCheckpoint) ||
                mapTimeTracker == null || !mapTimeTracker.TryApplyAuthorityHandoffBaseline(ack.ClockCheckpoint.Ticks))
            {
                Logger.Error("Rejected frozen campaign checkpoint {HandoffSequence} from {ControllerId}; no replacement authority was activated",
                    ack.HandoffSequence, player.ControllerId);
                return;
            }

            if (!TryCommitLease(lease, string.Empty, isPaused: true)) return;
            hasPendingFreeze = false;
            ResetPendingBarrier();
            Logger.Information("Applied final frozen campaign checkpoint {HandoffSequence} from {ControllerId}; reconciling the next writer",
                ack.HandoffSequence, player.ControllerId);
            ReconcileLease(coordinator.Select());
        }
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

    private void BroadcastPendingLease()
    {
        if (!pendingLeaseBroadcast.HasValue) return;

        var lease = pendingLeaseBroadcast.Value;
        try
        {
            network.SendAll(new NetworkAuthorityLeaseSnapshot(lease, sessionIdentity.RoomHostControllerId));
            pendingLeaseBroadcast = null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to distribute campaign authority lease epoch {Epoch}; will retry",
                lease.Epoch);
        }
    }
}
