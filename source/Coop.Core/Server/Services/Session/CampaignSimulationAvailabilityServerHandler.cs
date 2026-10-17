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

    private readonly IMessageBroker messageBroker;
    private readonly ICampaignAuthorityAvailabilityCoordinator coordinator;
    private readonly IConnectionCollection connections;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly object peerGate = new object();
    private readonly object selectionGate = new object();
    private readonly object leaseGate = new object();
    private readonly Dictionary<NetPeer, string> peerControllerIds = new Dictionary<NetPeer, string>();
    private readonly Timer reconciliationTimer;
    private bool campaignReady;
    private bool mapReady;
    private long localRevision;
    private bool hasLoggedSelection;
    private CampaignAuthoritySelection lastLoggedSelection;
    private AuthorityLease? pendingLeaseBroadcast;
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
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        reconciliationTimer = new Timer(_ => ReconcileCurrentAvailability(), null, 1000, 1000);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        reconciliationTimer.Dispose();
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        lock (peerGate) peerControllerIds.Clear();
        lock (selectionGate) { }
    }

    private void Handle_CampaignReady(MessagePayload<CampaignReady> _)
    {
        campaignReady = true;
        ReportLocalAvailability();
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        mapReady = payload.What.IsMapReady;
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

    private void ReportLocalAvailability()
    {
        if (!ModInformation.IsListenAuthority || !campaignReady) return;

        var controllerId = controllerIdProvider.ControllerId;
        bool localPlayerRegistered = playerManager.TryGetPlayer(controllerId, out _);
        var availability = mapReady && localPlayerRegistered
            ? CampaignSimulationAvailability.MapReady
            : CampaignSimulationAvailability.Unavailable;
        localRevision = localRevision == long.MaxValue ? 1 : localRevision + 1;
        if (coordinator.Report(controllerId, availability, localRevision)) ReconcileCurrentAvailability();
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
                // A paused lease has no previous writer to drain. Resume locally only when the
                // preferred room host is map-ready; promoting a remote client still requires the
                // explicit prepare/freeze/barrier handoff protocol.
                if (selection.IsPaused ||
                    !string.Equals(selection.ControllerId, roomHostControllerId, StringComparison.Ordinal) ||
                    !coordinator.IsFreshMapReady(roomHostControllerId))
                    return;

                TryCommitLease(currentLease, roomHostControllerId, isPaused: false);
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

            TryCommitLease(currentLease, string.Empty, isPaused: true);
        }
    }

    private void TryCommitLease(AuthorityLease currentLease, string authorityControllerId, bool isPaused)
    {
        if (currentLease.Epoch == long.MaxValue)
        {
            Logger.Error("Cannot advance campaign authority lease beyond epoch {Epoch}; campaign remains fenced",
                currentLease.Epoch);
            return;
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
            return;
        }

        pendingLeaseBroadcast = nextLease;
        BroadcastPendingLease();
        Logger.Information("Committed {LeaseState} campaign authority lease epoch {Epoch} for controller {ControllerId}",
            isPaused ? "paused" : "active", nextLease.Epoch, authorityControllerId);
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
