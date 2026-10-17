using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.PacketHandlers;
using Common.Util;
using Coop.Core.Server.Services.MobileParties.PacketHandlers;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Connections;

public class RemoteMovementOwnershipTests
{
    [Theory]
    [InlineData("PartyB", false, true, true)]
    [InlineData("PartyA", false, true, false)]
    [InlineData("PartyB", true, true, false)]
    [InlineData("PartyB", false, false, false)]
    public void OnlyCurrentPeerMayMoveItsOwnPartyWithoutTeleporting(
        string partyId, bool teleport, bool current, bool accepted)
    {
        var peer = ObjectHelper.SkipConstructor<NetPeer>();
        var oldPeer = ObjectHelper.SkipConstructor<NetPeer>();
        var player = new Player("B", "HeroB", "PartyB", "ClanB", "CharacterB");
        var players = new Mock<IPlayerManager>();
        players.Setup(p => p.TryGetPlayer(peer, out player)).Returns(true);
        var binding = current ? peer : oldPeer;
        players.Setup(p => p.TryGetPeer("B", out binding)).Returns(true);
        using var handler = new RequestMobilePartyBehaviorPacketHandler(
            Mock.Of<IPacketManager>(), Mock.Of<ISendCoalescer>(), Mock.Of<IMessageBroker>(),
            Mock.Of<INetwork>(), Mock.Of<IPartyBehaviorWireMapper>(), players.Object);
        var data = new PartyBehaviorUpdateData(partyId, default, null, default, default, default, default, default)
        { OriginControllerId = "forged-A", ForcePosition = teleport };
        Assert.Equal(accepted, handler.TryAuthorize(peer, ref data));
        if (accepted) Assert.Equal("B", data.OriginControllerId);
        Assert.False(handler.TryAuthorize(null, ref data));
    }
}
