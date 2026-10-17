using Common.Messaging;
using Common.Network.Session;
using Common.Session;
using Common.Tests.Utils;
using Coop.Core.Client.Messages;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.Time.Messages;
using Xunit;

namespace Coop.Tests.Session;

public class CampaignSimulationAvailabilityClientHandlerTests
{
    [Fact]
    public void ReportsOnlyAfterCatchupAndKeepsAvailabilityFresh()
    {
        var messageBroker = new TestMessageBroker();
        var network = new TestNetwork();
        var peer = network.CreatePeer();
        using var handler = new CampaignSimulationAvailabilityClientHandler(messageBroker, network);

        messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        Assert.False(network.SentNetworkMessages.ContainsKey(peer.Id));

        messageBroker.Publish(this, new ClientCampaignReady());
        // The periodic heartbeat renews the report even when map readiness has not changed.
        handler.SendCurrentAvailability();
        messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(true));
        messageBroker.Publish(this, new CampaignMapSimulationReadinessChanged(false));

        var reports = System.Linq.Enumerable.ToArray(
            network.GetPeerMessagesFromType<NetworkCampaignSimulationAvailabilityReport>(peer));
        Assert.Equal(4, reports.Length);
        Assert.Equal(CampaignSimulationAvailability.MapReady, reports[0].Availability);
        Assert.Equal(1, reports[0].Revision);
        Assert.Equal(CampaignSimulationAvailability.MapReady, reports[1].Availability);
        Assert.Equal(2, reports[1].Revision);
        Assert.Equal(CampaignSimulationAvailability.MapReady, reports[2].Availability);
        Assert.Equal(3, reports[2].Revision);
        Assert.Equal(CampaignSimulationAvailability.Unavailable, reports[3].Availability);
        Assert.Equal(4, reports[3].Revision);
    }
}
