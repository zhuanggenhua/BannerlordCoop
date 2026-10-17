using Common.Network.Session;
using Common.Serialization;
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
            serializer.Serialize(new NetworkAuthorityLeaseSnapshot(lease)));

        Assert.Equal(lease, received.ToLease());
    }
}
