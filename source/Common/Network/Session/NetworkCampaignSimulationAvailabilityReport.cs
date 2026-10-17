using Common.Messaging;
using Common.Session;
using ProtoBuf;

namespace Common.Network.Session;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignSimulationAvailabilityReport : IMessage
{
    [ProtoMember(1)]
    public readonly CampaignSimulationAvailability Availability;
    [ProtoMember(2)]
    public readonly long Revision;

    public NetworkCampaignSimulationAvailabilityReport(CampaignSimulationAvailability availability, long revision)
    {
        Availability = availability;
        Revision = revision;
    }
}
