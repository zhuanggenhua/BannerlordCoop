using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.Inventory.Handlers;
using GameInterface.Services.Inventory.Interfaces;
using GameInterface.Services.Inventory.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Villages.Interfaces;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public class ListenAuthorityTradeTests
{
    [Fact]
    public void ReplayedOrStaleCommitIsRejectedButReconnectedPeerHasFreshWindow()
    {
        using var handler = new TradeHandler(Mock.Of<IInventoryLogicInterface>(), Mock.Of<IMessageBroker>(),
            Mock.Of<IObjectManager>(), Mock.Of<INetwork>(), Mock.Of<IVillageHostileActionInterface>(), Mock.Of<IPlayerManager>());
        var peer = new object();
        Assert.True(handler.TryBeginCommit(peer, 8));
        Assert.False(handler.TryBeginCommit(peer, 8));
        Assert.False(handler.TryBeginCommit(peer, 7));
        Assert.False(handler.TryBeginCommit(peer, 0));
        Assert.True(handler.TryBeginCommit(peer, 9));
        Assert.True(handler.TryBeginCommit(new object(), 1));
        Assert.True(handler.TryBeginCommit(null, 1));
        Assert.False(handler.TryBeginCommit(null, 1));
    }
    [Theory]
    [InlineData(SessionMode.ListenAuthority, true)]
    [InlineData(SessionMode.RemoteReplica, false)]
    public void LocalCommitHasExactlyOneDestinationWithoutNetworkEcho(SessionMode mode, bool local)
    {
        var previous = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = mode;
            var broker = new Mock<IMessageBroker>();
            var network = new Mock<INetwork>();
            using var handler = new TradeHandler(Mock.Of<IInventoryLogicInterface>(), broker.Object,
                Mock.Of<IObjectManager>(), network.Object, Mock.Of<IVillageHostileActionInterface>(), Mock.Of<IPlayerManager>());
            handler.DispatchCompleteTrade(default);
            broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<CompleteTrade>()), local ? Times.Once() : Times.Never());
            network.Verify(n => n.SendAll(It.IsAny<CompleteTrade>()), local ? Times.Never() : Times.Once());
        }
        finally { ModInformation.SessionMode = previous; }
    }
}
