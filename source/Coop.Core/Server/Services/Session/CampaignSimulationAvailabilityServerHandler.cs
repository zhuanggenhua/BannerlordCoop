using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Common.Session;
using Coop.Core.Server.Connections;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Time.Messages;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;

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
    private readonly object peerGate = new object();
    private readonly object localReportGate = new object();
    private readonly object selectionGate = new object();
    private readonly object leaseGate = new object();
    private readonly Dictionary<NetPeer, string> peerControllerIds = new Dictionary<NetPeer, string>();
    private readonly Timer availabilityTimer;
    private bool campaignReady;
    private bool mapReady;
    private long localRevision;
    private bool hasLoggedSelection;
    private CampaignAuthoritySelection lastLoggedSelection;
    private AuthorityLease? pendingLeaseBroadcast;
    private long pendingBarrierSequence;
    private long pendingBarrierEpoch;
    private string pendingBarrierAuthorityId = string.Empty;
    private string pendingBarrierCandidateControllerId = string.Empty;
    private bool hasPendingBarrier;
    private bool pendingBarrierAcknowledged;
    private int heartbeatQueued;
    private int disposed;

    public CampaignSimulationAvailabilityServerHandler(
        IMessageBroker messageBroker,
        ICampaignAuthorityAvailabilityCoordinator coordinator,
        IConnectionCollection connections,
        IPlayerManager playerManager,
        IControllerIdProvider controllerIdProvider,
        INetwork network = null,
        ISessionIdentity sessionIdentity = null)
    {
        this.messageBroker = messageBroker;
        this.coordinator = coordinator;
        this.connections = connections;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        availabilityTimer = new Timer(_ => QueueLocalHeartbeat(), null, 1000, 1000);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        availabilityTimer.Dispose();
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityHandoffBarrierApplied>(Handle_HandoffBarrierApplied);
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
        if (!coordinator.Report(player.ControllerId, report.Availability, report.Revision))
        {
            Logger.Warning("Rejected stale campaign availability report from controller {ControllerId} at revision {Revision}",
                player.ControllerId, report.Revision);
            return;
        }

        lock (peerGate) peerControllerIds[peer] = player.ControllerId;
        ReconcileCurrentAvailability();
    }

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (payload.What.PlayerId == null) return;

        string controllerId;
        lock (peerGate)
        {
            if (!peerControllerIds.TryGetValue(payload.What.PlayerId, out controllerId)) return;
            peerControllerIds.Remove(payload.What.PlayerId);
        }

        if (playerManager.TryGetPeer(controllerId, out var currentPeer) &&
            currentPeer != payload.What.PlayerId)
            return;

        if (coordinator.Remove(controllerId)) ReconcileCurrentAvailability();
    }

    private void QueueLocalHeartbeat()
    {
        if (Volatile.Read(ref disposed) != 0 || !ModInformation.IsListenAuthority) return;

        bool queueHeartbeat;
        lock (localReportGate)
        {
            queueHeartbeat = campaignReady && Interlocked.Exchange(ref heartbeatQueued, 1) == 0;
        }

        if (queueHeartbeat)
        {
            try
            {
                // Read native player registration and refresh readiness on the game thread. If that
                // thread stops pumping, the room will stop receiving fresh local-ready reports.
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

        // Keep expiry decisions moving even if the game thread stopped pumping and the local
        // heartbeat could not run. A stale host report must be able to fence its own lease.
        ReconcileCurrentAvailability();
    }

    private void ReportLocalAvailability(bool reconcile = true)
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
        if (!ModInformation.IsListenAuthority || network == null || sessionIdentity == null) return;

        lock (leaseGate)
        {
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

            // This interim reconciliation can fence only the local room-host authority. A remote
            // lease holder must acknowledge a freeze and ordered sync barrier before the relay may
            // replace its lease; until that handoff exists, never activate a different writer here.
            if (!string.Equals(currentLease.AuthorityControllerId, localControllerId, StringComparison.Ordinal))
                return;

            if (!selection.IsPaused &&
                coordinator.IsFreshMapReady(currentLease.AuthorityControllerId))
                return;

            if (!TryCommitLease(currentLease, string.Empty, isPaused: true)) return;

            if (!selection.IsPaused &&
                !string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) &&
                coordinator.IsFreshMapReady(selection.ControllerId) &&
                sessionIdentity.TryGetConfirmedLease(out var pausedLease))
                TrySendHandoffBarrier(pausedLease, selection.ControllerId);
        }
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

        pendingLeaseBroadcast = nextLease;
        BroadcastPendingLease();
        Logger.Information("Committed {LeaseState} campaign authority lease epoch {Epoch} for controller {ControllerId}",
            isPaused ? "paused" : "active", nextLease.Epoch, authorityControllerId);
        return true;
    }

    private void TrySendHandoffBarrier(AuthorityLease pausedLease, string candidateControllerId)
    {
        if (!pausedLease.IsPaused || string.IsNullOrWhiteSpace(candidateControllerId) ||
            string.Equals(candidateControllerId, sessionIdentity.RoomHostControllerId, StringComparison.Ordinal) ||
            network == null || !coordinator.IsFreshMapReady(candidateControllerId))
            return;

        if (hasPendingBarrier && pendingBarrierEpoch == pausedLease.Epoch &&
            string.Equals(pendingBarrierAuthorityId, pausedLease.AuthorityId, StringComparison.Ordinal) &&
            string.Equals(pendingBarrierCandidateControllerId, candidateControllerId, StringComparison.Ordinal))
            return;

        if (!sessionIdentity.TryGetConfirmedLease(out var currentLease) || currentLease != pausedLease)
            return;

        NetPeer candidatePeer = null;
        lock (peerGate)
        {
            foreach (var entry in peerControllerIds)
            {
                if (string.Equals(entry.Value, candidateControllerId, StringComparison.Ordinal))
                {
                    candidatePeer = entry.Key;
                    break;
                }
            }
        }

        if (candidatePeer == null || !connections.HasCompletedCampaignSynchronization(candidatePeer))
            return;

        if (!TryGetNextBarrierSequence(out var sequence))
        {
            Logger.Error("Cannot issue another campaign handoff barrier; sequence space is exhausted");
            return;
        }

        var barrier = new NetworkCampaignAuthorityHandoffBarrier(
            pausedLease.RoomId,
            pausedLease.AuthorityId,
            pausedLease.Epoch,
            candidateControllerId,
            sequence);

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
        if (payload.Who is not NetPeer peer ||
            !connections.HasCompletedCampaignSynchronization(peer) ||
            !playerManager.TryGetPlayer(peer, out var player))
            return;

        lock (selectionGate)
        {
            lock (leaseGate)
            {
                if (!hasPendingBarrier || pendingBarrierAcknowledged ||
                    !sessionIdentity.TryGetConfirmedLease(out var currentLease) || !currentLease.IsPaused ||
                    currentLease.Epoch != pendingBarrierEpoch ||
                    !coordinator.IsFreshMapReady(player.ControllerId))
                    return;

                var ack = payload.What;
                if (ack.BarrierSequence != pendingBarrierSequence ||
                    ack.PausedEpoch != pendingBarrierEpoch ||
                    !string.Equals(ack.RoomId, currentLease.RoomId, StringComparison.Ordinal) ||
                    !string.Equals(ack.PausedAuthorityId, pendingBarrierAuthorityId, StringComparison.Ordinal) ||
                    !string.Equals(ack.CandidateControllerId, pendingBarrierCandidateControllerId, StringComparison.Ordinal) ||
                    !string.Equals(player.ControllerId, pendingBarrierCandidateControllerId, StringComparison.Ordinal))
                    return;

                lock (peerGate)
                {
                    if (!peerControllerIds.TryGetValue(peer, out var knownControllerId) ||
                        !string.Equals(knownControllerId, player.ControllerId, StringComparison.Ordinal))
                        return;
                }

                var selection = coordinator.Select();
                if (selection.IsPaused ||
                    !string.Equals(selection.ControllerId, player.ControllerId, StringComparison.Ordinal))
                    return;

                pendingBarrierAcknowledged = true;
                Logger.Information("Controller {ControllerId} applied campaign handoff barrier {BarrierSequence} for paused epoch {Epoch}; campaign remains paused until authority-side world replication is available",
                    player.ControllerId, pendingBarrierSequence, pendingBarrierEpoch);
            }
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
