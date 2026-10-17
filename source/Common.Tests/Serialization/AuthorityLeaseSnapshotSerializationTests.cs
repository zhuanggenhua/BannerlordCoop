using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Xunit;

namespace Common.Tests.Serialization;

public class AuthorityLeaseSnapshotSerializationTests
{
    [Fact]
    public void Snapshot_RoundTripsLeaseFields()
    {
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 7);
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());

        var received = serializer.Deserialize<NetworkAuthorityLeaseSnapshot>(
            serializer.Serialize(new NetworkAuthorityLeaseSnapshot(lease, "room-host-a")));

        Assert.Equal(lease, received.ToLease());
        Assert.Equal("room-host-a", received.RoomHostControllerId);
    }

    [Fact]
    public void CampaignAvailabilityReport_RoundTripsStateAndRevision()
    {
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());

        var received = serializer.Deserialize<NetworkCampaignSimulationAvailabilityReport>(
            serializer.Serialize(new NetworkCampaignSimulationAvailabilityReport(
                CampaignSimulationAvailability.MapReady,
                42)));

        Assert.Equal(CampaignSimulationAvailability.MapReady, received.Availability);
        Assert.Equal(42, received.Revision);
    }
}
