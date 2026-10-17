using Xunit;

namespace Common.Tests;

[Collection("ModInformation")]
public class ListenAuthorityModeTests
{
    [Fact]
    public void ListenAuthorityRetainsAuthorityWithoutBecomingRemoteReplica()
    {
        var previous = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            Assert.True(ModInformation.IsServer);
            Assert.False(ModInformation.IsClient);
            Assert.False(ModInformation.IsDedicatedAuthority);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void RoomAndAuthorityHaveSeparateSessionIdentities()
    {
        var identity = new SessionIdentity();
        Assert.NotEqual(identity.RoomId, identity.AuthorityId);
        Assert.NotEqual("Server", identity.AuthorityId);
        Assert.NotEqual(identity.RoomId, new SessionIdentity().RoomId);
        Assert.Equal(1, identity.Epoch);
        Assert.False(identity.IsConfirmed);
    }

    [Fact]
    public void FirstAuthorityLeaseConfirmsTheRoomAndAuthority()
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);

        Assert.True(identity.TryApply(lease));
        Assert.True(identity.IsConfirmed);
        Assert.Equal(lease, identity.CurrentLease);
    }

    [Fact]
    public void ConfirmedLeaseSnapshotDoesNotExposeUnconfirmedIdentity()
    {
        var identity = new SessionIdentity();

        Assert.False(identity.TryGetConfirmedLease(out var unconfirmedLease));
        Assert.False(unconfirmedLease.IsValid);

        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
        Assert.True(identity.TryApply(lease));
        Assert.True(identity.TryGetConfirmedLease(out var confirmedLease));
        Assert.Equal(lease, confirmedLease);
    }

    [Fact]
    public void AuthorityLeaseRejectsStaleEpochAndConflictingSameEpoch()
    {
        var identity = new SessionIdentity();
        var current = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        Assert.True(identity.TryRestore(current));

        Assert.False(identity.TryApply(new AuthorityLease("room-a", "authority-a", "controller-a", 1)));
        Assert.False(identity.TryApply(new AuthorityLease("room-a", "authority-other", "controller-b", 2)));
        Assert.Equal(current, identity.CurrentLease);
    }

    [Fact]
    public void RestoringSavedLeaseCannotReplaceAnAlreadyConfirmedRoom()
    {
        var identity = new SessionIdentity();
        var current = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
        Assert.True(identity.TryApply(current));

        Assert.False(identity.TryRestore(new AuthorityLease("room-old", "authority-old", "controller-old", 1)));
        Assert.Equal(current, identity.CurrentLease);
    }

    [Fact]
    public void HigherEpochRequiresNewAuthorityIdentityAndKeepsRoomIdentity()
    {
        var identity = new SessionIdentity();
        var initial = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
        Assert.True(identity.TryRestore(initial));

        Assert.False(identity.TryApply(new AuthorityLease("room-b", "authority-b", "controller-b", 2)));
        Assert.False(identity.TryApply(new AuthorityLease("room-a", "authority-a", "controller-b", 2)));
        Assert.True(identity.TryApply(new AuthorityLease("room-a", "authority-b", "controller-b", 2)));
        Assert.Equal("authority-b", identity.AuthorityId);
        Assert.Equal(2, identity.Epoch);
    }

    [Fact]
    public void LocalAuthorityControllerCanBeSetOnceWithoutChangingLeaseEpoch()
    {
        var identity = new SessionIdentity();
        var authorityId = identity.AuthorityId;

        Assert.True(identity.TrySetAuthorityControllerId("controller-a"));
        Assert.True(identity.TrySetAuthorityControllerId("controller-a"));
        Assert.False(identity.TrySetAuthorityControllerId("controller-b"));
        Assert.Equal(authorityId, identity.AuthorityId);
        Assert.Equal(1, identity.Epoch);
        Assert.Equal("controller-a", identity.AuthorityControllerId);
    }

    [Fact]
    public void RoomHostControllerIsStableAndLeaseSnapshotAppliesItAtomically()
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);

        Assert.True(identity.TryApply(lease, "room-host-a"));
        Assert.False(identity.TryApply(
            new AuthorityLease("room-a", "authority-b", "controller-b", 2),
            "room-host-b"));

        Assert.Equal(lease, identity.CurrentLease);
        Assert.Equal("room-host-a", identity.RoomHostControllerId);
    }

    [Fact]
    public void SnapshotWithoutRoomHostDoesNotClearKnownRoomHost()
    {
        var identity = new SessionIdentity();
        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);

        Assert.True(identity.TryApply(lease, "room-host-a"));
        Assert.True(identity.TryApply(lease, string.Empty));

        Assert.Equal("room-host-a", identity.RoomHostControllerId);
    }

    [Fact]
    public void EmptyRoomHostDoesNotReplaceAnExistingIdentity()
    {
        var identity = new SessionIdentity();

        Assert.True(identity.TrySetRoomHostControllerId("room-host-a"));
        Assert.True(identity.TrySetRoomHostControllerId(string.Empty));

        Assert.Equal("room-host-a", identity.RoomHostControllerId);
    }
}
