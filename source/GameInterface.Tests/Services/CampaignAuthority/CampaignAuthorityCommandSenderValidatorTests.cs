using Common;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System.Runtime.CompilerServices;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityCommandSenderValidatorTests
{
    static CampaignAuthorityCommandSenderValidatorTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void IsRoomHost_RequiresCurrentPeerForRoomHostController()
    {
        using var network = new TestNetwork();
        var sender = network.CreatePeer();
        var host = new Player("controller-host", "hero-host", "party-host", "clan-host", "character-host");
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPlayer(sender, out host)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("controller-host", out sender)).Returns(true);
        var identity = new Mock<ISessionIdentity>();
        identity.Setup(value => value.RoomHostControllerId).Returns("controller-host");

        Assert.True(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, identity.Object));
    }

    [Fact]
    public void IsRoomHost_RejectsOtherControllerAndSupersededPeer()
    {
        using var network = new TestNetwork();
        var sender = network.CreatePeer();
        var currentPeer = network.CreatePeer();
        var nonHost = new Player("controller-other", "hero-other", "party-other", "clan-other", "character-other");
        var players = new Mock<IPlayerManager>();
        players.Setup(manager => manager.TryGetPlayer(sender, out nonHost)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("controller-other", out sender)).Returns(true);
        var identity = new Mock<ISessionIdentity>();
        identity.Setup(value => value.RoomHostControllerId).Returns("controller-host");

        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, identity.Object));

        var host = new Player("controller-host", "hero-host", "party-host", "clan-host", "character-host");
        players.Setup(manager => manager.TryGetPlayer(sender, out host)).Returns(true);
        players.Setup(manager => manager.TryGetPeer("controller-host", out currentPeer)).Returns(true);

        Assert.False(CampaignAuthorityCommandSenderValidator.IsRoomHost(sender, players.Object, identity.Object));
    }
}
