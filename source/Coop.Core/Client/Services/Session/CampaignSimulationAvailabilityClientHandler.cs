using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using GameInterface.Services.Time.Messages;
using Serilog;
using Coop.Core.Client.Messages;
using System;
using System.Threading;

namespace Coop.Core.Client.Services.Session;

public sealed class CampaignSimulationAvailabilityClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSimulationAvailabilityClientHandler>();
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly object gate = new object();
    private readonly Timer heartbeatTimer;
    private bool campaignSynchronized;
    private bool mapReady;
    private bool hasReported;
    private CampaignSimulationAvailability lastReportedAvailability;
    private long revision;
    private int heartbeatQueued;
    private int disposed;

    public CampaignSimulationAvailabilityClientHandler(IMessageBroker messageBroker, INetwork network)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        messageBroker.Subscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        heartbeatTimer = new Timer(_ => QueueHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        heartbeatTimer.Dispose();
        messageBroker.Unsubscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
    }

    private void Handle_ClientCampaignReady(MessagePayload<ClientCampaignReady> _)
    {
        lock (gate) campaignSynchronized = true;
        SendCurrentAvailability();
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        bool shouldReport;
        lock (gate)
        {
            mapReady = payload.What.IsMapReady;
            shouldReport = campaignSynchronized;
        }

        if (shouldReport) SendCurrentAvailability();
    }

    internal void QueueHeartbeat()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            if (!campaignSynchronized || Interlocked.Exchange(ref heartbeatQueued, 1) != 0) return;
        }

        try
        {
            // Read readiness and send on the game thread. If that thread stops pumping, this peer
            // stops renewing its map-ready report and will age out at the room host.
            GameThread.EnqueueSafe(() =>
            {
                try
                {
                    if (Volatile.Read(ref disposed) == 0) SendCurrentAvailability();
                }
                finally
                {
                    Interlocked.Exchange(ref heartbeatQueued, 0);
                }
            }, context: nameof(CampaignSimulationAvailabilityClientHandler));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref heartbeatQueued, 0);
            Logger.Warning(ex, "Could not queue campaign availability heartbeat");
        }
    }

    internal void SendCurrentAvailability()
    {
        CampaignSimulationAvailability availability;
        bool shouldLog;
        long currentRevision;
        lock (gate)
        {
            if (!campaignSynchronized || Volatile.Read(ref disposed) != 0) return;

            availability = mapReady
                ? CampaignSimulationAvailability.MapReady
                : CampaignSimulationAvailability.Unavailable;
            // An unchanged heartbeat may reuse the revision; the coordinator refreshes freshness
            // for an identical report. Saturate rather than wrapping into a stale revision.
            if (revision < long.MaxValue) revision++;
            currentRevision = revision;
            // Keep revision assignment and reliable queue insertion in one critical section so a
            // concurrent readiness change cannot be sent ahead of an older heartbeat.
            network.SendAll(new NetworkCampaignSimulationAvailabilityReport(availability, currentRevision));
            shouldLog = !hasReported || availability != lastReportedAvailability;
            hasReported = true;
            lastReportedAvailability = availability;
        }

        if (shouldLog)
        {
            Logger.Information("Reported campaign simulation availability {Availability} at revision {Revision}",
                availability, currentRevision);
        }
    }
}
