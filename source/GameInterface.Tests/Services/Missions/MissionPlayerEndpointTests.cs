using GameInterface.Services.Missions;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Missions;

public class MissionPlayerEndpointTests
{
    [Fact]
    public void LocalEndpointCarriesOnlyTheControllerIdentity()
    {
        var endpoint = new LocalPlayerEndpoint("native-player");
        Assert.Equal("native-player", endpoint.ControllerId);
        Assert.Null(endpoint.Peer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EndpointRequiresAController(string controllerId)
    {
        Assert.Throws<ArgumentException>(() => new LocalPlayerEndpoint(controllerId));
    }

    [Fact]
    public void RemoteEndpointRequiresAnActualPeer()
    {
        Assert.Throws<ArgumentNullException>(() => new RemotePeerEndpoint("remote", null));
    }
}
