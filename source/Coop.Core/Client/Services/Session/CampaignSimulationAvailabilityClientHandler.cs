using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using GameInterface.Services.Time.Messages;
using Serilog;
using Coop.Core.Client.Messages;

namespace Coop.Core.Client.Services.Session;

public sealed class CampaignSimulationAvailabilityClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignSimulationAvailabilityClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private bool campaignSynchronized;
    private bool mapReady;
    private bool hasReported;
    private CampaignSimulationAvailability lastReportedAvailability;
    private long revision;

    public CampaignSimulationAvailabilityClientHandler(IMessageBroker messageBroker, INetwork network)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        messageBroker.Subscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
    }

    private void Handle_ClientCampaignReady(MessagePayload<ClientCampaignReady> _)
    {
        campaignSynchronized = true;
        SendCurrentAvailability();
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        mapReady = payload.What.IsMapReady;
        if (campaignSynchronized) SendCurrentAvailability();
    }

    private void SendCurrentAvailability()
    {
        var availability = mapReady
            ? CampaignSimulationAvailability.MapReady
            : CampaignSimulationAvailability.Unavailable;
        revision = revision == long.MaxValue ? 1 : revision + 1;
        network.SendAll(new NetworkCampaignSimulationAvailabilityReport(availability, revision));

        if (!hasReported || availability != lastReportedAvailability)
        {
            Logger.Information("Reported campaign simulation availability {Availability} at revision {Revision}",
                availability, revision);
        }

        hasReported = true;
        lastReportedAvailability = availability;
    }
}
