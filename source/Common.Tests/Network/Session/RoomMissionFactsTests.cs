using Common.Network.Session;
using ProtoBuf;
using Xunit;

namespace Common.Tests.Network.Session;

public class RoomMissionFactsTests
{
    [Fact]
    public void ExplicitEmptySnapshotIsValidAndDefaultVersionIsNot()
    {
        Assert.False(default(RoomMissionFactsVersion).IsValid);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 0, Array.Empty<RoomMissionMember>(), out var snapshot));
        Assert.True(snapshot.IsValid);
        using var bytes = new MemoryStream();
        Serializer.Serialize(bytes, new NetworkRoomMissionFactsSnapshot(snapshot));
        bytes.Position = 0;
        var restored = Serializer.Deserialize<NetworkRoomMissionFactsSnapshot>(bytes);
        Assert.NotNull(restored.Snapshot);
        Assert.True(restored.Snapshot.IsValid);
        Assert.Equal(snapshot.Version, restored.Snapshot.Version);
        Assert.Empty(restored.Snapshot.Members);
    }

    [Fact]
    public void HashIsCanonicalAndBindsRoomRevisionAndMembershipGeneration()
    {
        var a = new RoomMissionMember("A", "town", 1);
        var b = new RoomMissionMember("B", "battle", 2);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 7, new[] { b, a }, out var first));
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 7, new[] { a, b }, out var reordered));
        Assert.Equal(first.Version, reordered.Version);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("other", 7, new[] { a, b }, out var otherRoom));
        Assert.NotEqual(first.Version.Sha256, otherRoom.Version.Sha256);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 8, new[] { a, b }, out var otherRevision));
        Assert.NotEqual(first.Version.Sha256, otherRevision.Version.Sha256);
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 7,
            new[] { a, new RoomMissionMember("B", "battle", 3) }, out var reconnect));
        Assert.NotEqual(first.Version, reconnect.Version);
        using var bytes = new MemoryStream();
        Serializer.Serialize(bytes, new NetworkRoomMissionFactsApplied(first.Version));
        bytes.Position = 0;
        Assert.Equal(first.Version, Serializer.Deserialize<NetworkRoomMissionFactsApplied>(bytes).Version);
    }

    [Fact]
    public void InvalidDuplicateAndTamperedRowsCannotAttestACompleteSnapshot()
    {
        Assert.False(RoomMissionFactsSnapshot.TryCreate("room", 0, null!, out _));
        Assert.False(RoomMissionFactsSnapshot.TryCreate("room", -1, Array.Empty<RoomMissionMember>(), out _));
        Assert.False(RoomMissionFactsSnapshot.TryCreate("room", 0, new[] { default(RoomMissionMember) }, out _));
        var member = new RoomMissionMember("A", "town", 1);
        Assert.False(RoomMissionFactsSnapshot.TryCreate("room", 0, new[] { member, member }, out _));
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 0, new[] { member }, out var snapshot));
        snapshot.Members[0] = new RoomMissionMember("B", "town", 1);
        Assert.False(snapshot.IsValid);
    }

    [Fact]
    public void LengthPrefixedFieldsCannotCollideOnSeparators()
    {
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 1,
            new[] { new RoomMissionMember("A|B", "C", 1) }, out var first));
        Assert.True(RoomMissionFactsSnapshot.TryCreate("room", 1,
            new[] { new RoomMissionMember("A", "B|C", 1) }, out var second));
        Assert.NotEqual(first.Version.Sha256, second.Version.Sha256);
    }
}
