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
    }
}
