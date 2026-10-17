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
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Common.Util;

namespace GameInterface.Tests.Services.Players;

[Collection(ModInformationRoleCollection.Name)]
public class ListenAuthorityTradeTests
{
    [Fact]
    public void CommitSnapshotSurvivesNativePreviewResetBeforeDeferredApply()
    {
        var item = new ItemObject("grain");
        var roster = new ItemRoster();
        roster.AddToCounts(item, 3);
        var commit = new CompleteTrade(null, false, null, roster._data, roster._data, null,
            true, false, false, null, null, 0, 0, null, null, true, null, null, null);
        roster.Clear();
        Assert.Equal(0, roster.GetItemNumber(item));
        ObjectHelper.SkipConstructor<InventoryLogicInterface>().UpdateRosterWithData(roster, commit.ToItemRosterData);
        Assert.Equal(3, roster.GetItemNumber(item));
        Assert.NotSame(commit.FromItemRosterData, commit.ToItemRosterData);
    }
    [Fact]
    public void LocalRosterCommitPreservesDataWhenSourceIsItsOwnBackingArray()
    {
        var item = new ItemObject("grain");
        var roster = new ItemRoster();
        roster.AddToCounts(item, 3);
        var inventory = ObjectHelper.SkipConstructor<InventoryLogicInterface>();
        inventory.UpdateRosterWithData(roster, roster._data);
        Assert.Equal(3, roster.GetItemNumber(item));
    }
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
            // Use the suite's existing pump; never replace its process-wide game-thread identity.
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
                typeof(Coop.Tests.Server.Connections.RemoteMovementOwnershipTests).Module.ModuleHandle);
            ModInformation.SessionMode = mode;
            var broker = new Mock<IMessageBroker>();
            var network = new Mock<INetwork>();
            using var applied = new System.Threading.ManualResetEventSlim();
            broker.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CompleteTrade>())).Callback(() => applied.Set());
            using var handler = new TradeHandler(Mock.Of<IInventoryLogicInterface>(), broker.Object,
                Mock.Of<IObjectManager>(), network.Object, Mock.Of<IVillageHostileActionInterface>(), Mock.Of<IPlayerManager>());
            GameThread.Run(() =>
            {
                handler.DispatchCompleteTrade(default);
                broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<CompleteTrade>()), Times.Never());
            }, blocking: true);
            if (local) Assert.True(applied.Wait(System.TimeSpan.FromSeconds(5)));
            broker.Verify(b => b.Publish(It.IsAny<object>(), It.IsAny<CompleteTrade>()), local ? Times.Once() : Times.Never());
            network.Verify(n => n.SendAll(It.IsAny<CompleteTrade>()), local ? Times.Never() : Times.Once());
        }
        finally { ModInformation.SessionMode = previous; }
    }
}
