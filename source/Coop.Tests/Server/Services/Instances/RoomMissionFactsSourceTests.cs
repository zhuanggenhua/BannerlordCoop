using Common.Network.Session;
using Coop.Core.Server.Services.Instances;
using GameInterface.Services.Players;
using LiteNetLib;
using Moq;
using System;
using System.Net;
using System.Reflection;
using Xunit;

namespace Coop.Tests.Server.Services.Instances;

public class RoomMissionFactsSourceTests
{
    private static NetPeer Peer(int id) => (NetPeer)typeof(NetPeer).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(NetManager), typeof(IPEndPoint), typeof(int) }, null)!
        .Invoke(new object[] { new NetManager(null), new IPEndPoint(IPAddress.Loopback, 40000 + id), id });

    [Fact]
    public void SourceHasNoSnapshotBeforeRoomConfirmation()
    {
        var session = new SessionIdentity();
        var manager = new MissionManager(Mock.Of<IPlayerManager>(), null, session);
        Assert.False(manager.TryCapture(out _));
        Assert.True(session.TryConfirmCurrentLease());
        Assert.True(manager.TryCapture(out var snapshot));
        Assert.True(snapshot.IsValid);
        Assert.Empty(snapshot.Members);
        Assert.True(manager.CanActivateCampaign(snapshot.Version));
        Assert.True(manager.TryEnterMission(Peer(7), "A", "town", out _));
        Assert.True(manager.TryLeaveMission(Peer(8), "A", "town", out _) == false);
        Assert.False(manager.CanActivateCampaign(snapshot.Version));
    }

    [Fact]
    public void DuplicateEntryDoesNotAdvanceButReconnectAndLeaveDo()
    {
        var session = new SessionIdentity();
        Assert.True(session.TryConfirmCurrentLease());
        var manager = new MissionManager(Mock.Of<IPlayerManager>(), null, session);
        var first = Peer(1);
        var next = Peer(2);
        int changes = 0;
        manager.FactsChanged += () =>
        {
            Assert.True(manager.TryCapture(out _));
            changes++;
        };
        Assert.True(manager.TryEnterMission(first, "A", "battle", out _));
        Assert.True(manager.TryCapture(out var entered));
        Assert.False(manager.CanActivateCampaign(entered.Version));
        Assert.True(manager.TryEnterMission(first, "A", "battle", out _));
        Assert.True(manager.TryCapture(out var duplicate));
        Assert.Equal(entered.Version, duplicate.Version);
        Assert.Equal(1, changes);
        Assert.True(manager.TryEnterMission(next, "A", "battle", out _));
        Assert.True(manager.TryCapture(out var reconnected));
        Assert.True(reconnected.Version.Revision > entered.Version.Revision);
        Assert.True(reconnected.Members[0].Generation > entered.Members[0].Generation);
        Assert.Empty(manager.HandleDisconnect(first));
        Assert.True(manager.HasApplied(reconnected.Version));
        Assert.True(manager.TryLeaveMission(next, "A", "battle", out _));
        Assert.True(manager.TryCapture(out var left));
        Assert.True(left.Version.Revision > reconnected.Version.Revision);
        Assert.Empty(left.Members);
        Assert.False(manager.CanActivateCampaign(entered.Version));
        Assert.False(manager.CanActivateCampaign(reconnected.Version));
        Assert.True(manager.CanActivateCampaign(left.Version));
        Assert.Equal(3, changes);
    }
}
