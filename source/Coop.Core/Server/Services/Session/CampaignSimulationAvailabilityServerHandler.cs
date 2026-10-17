using Common;
using Common.Logging;
using Common.Messaging;
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
using System.Collections.Generic;

namespace Coop.Core.Server.Services.Session;

public sealed class CampaignSimulationAvailabilityServerHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSimulationAvailabilityServerHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ICampaignAuthorityAvailabilityCoordinator coordinator;
    private readonly IConnectionCollection connections;
    private readonly IPlayerManager playerManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly object peerGate = new object();
    private readonly Dictionary<NetPeer, string> peerControllerIds = new Dictionary<NetPeer, string>();
    private bool campaignReady;
    private bool mapReady;
    private long localRevision;
    private bool hasLoggedSelection;
    private CampaignAuthoritySelection lastLoggedSelection;

    public CampaignSimulationAvailabilityServerHandler(
        IMessageBroker messageBroker,
        ICampaignAuthorityAvailabilityCoordinator coordinator,
        IConnectionCollection connections,
        IPlayerManager playerManager,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.coordinator = coordinator;
        this.connections = connections;
        this.playerManager = playerManager;
        this.controllerIdProvider = controllerIdProvider;
        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignSimulationAvailabilityReport>(Handle_AvailabilityReport);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        lock (peerGate) peerControllerIds.Clear();
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
        LogSelectionIfChanged();
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

        if (coordinator.Remove(controllerId)) LogSelectionIfChanged();
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
        if (coordinator.Report(controllerId, availability, localRevision)) LogSelectionIfChanged();
    }

    private void LogSelectionIfChanged()
    {
        var selection = coordinator.Select();
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
}
