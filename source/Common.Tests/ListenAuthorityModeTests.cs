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
}
