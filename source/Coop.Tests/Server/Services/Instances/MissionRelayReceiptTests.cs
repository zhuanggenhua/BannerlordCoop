using Common.Util;
using Coop.Core.Server.Services.Instances;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using Serilog;
using System;
using System.Net;
using System.Reflection;
using Xunit;

namespace Coop.Tests.Server.Services.Instances;

public class MissionRelayReceiptTests
{
    [Fact]
    public void ReceiptUsesActualPeerAndIdempotentEntryKeepsItsGeneration()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var receipt));
        Assert.Same(fixture.Remote, receipt.Peer);
        Assert.Equal("B", receipt.ControllerId);
        Assert.Equal("A", receipt.TargetControllerId);
        Assert.NotEqual(Guid.Empty, receipt.SourceCredential);
        Assert.NotEqual(Guid.Empty, receipt.TargetCredential);
        Assert.True(receipt.SourceGeneration > 0);
        Assert.True(receipt.TargetGeneration > 0);
        Assert.True(fixture.Manager.TryEnterMission(fixture.Remote, "B", "battle", out var duplicate));
        Assert.Equal(MissionEntryStatus.Unchanged, duplicate.Status);
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var again));
        Assert.Equal(receipt.SourceGeneration, again.SourceGeneration);
        Assert.Equal(receipt.TargetGeneration, again.TargetGeneration);
        Assert.True(fixture.Manager.IsCurrent(receipt));
    }

    [Fact]
    public void LeaveAndRejoinTheSameInstanceInvalidatesBothSourceAndTargetReceipts()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var first));
        Assert.True(fixture.Manager.TryLeaveMission(fixture.Remote, "B", "battle", out _));
        Assert.True(fixture.Manager.TryEnterMission(fixture.Remote, "B", "battle", out _));
        Assert.False(fixture.Manager.IsCurrent(first));
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var second));
        Assert.NotEqual(first.SourceCredential, second.SourceCredential);
        Assert.True(second.SourceGeneration > first.SourceGeneration);
        var local = new LocalPlayerEndpoint("A");
        Assert.True(fixture.Manager.TryLeaveMission(local, "battle", out _));
        Assert.True(fixture.Manager.TryEnterMission(local, "battle", out _));
        Assert.False(fixture.Manager.IsCurrent(second));
    }

    [Fact]
    public void DifferentInstanceTargetPeerOrIssuerCannotAuthorizeLocalDelivery()
    {
        var fixture = new Fixture();
        Assert.False(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "other", "A", out _));
        Assert.False(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "B", out _));
        Assert.False(fixture.Manager.TryCaptureLocalDelivery(CreatePeer(2), "battle", "A", out _));
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var receipt));
        Assert.False(new Fixture().Manager.IsCurrent(receipt));
        Assert.False(fixture.Manager.IsCurrent(new ForeignReceipt(receipt)));
    }

    [Fact]
    public void PeerReplacementAndRevocationRejectOldReceipts()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(fixture.Remote, "battle", "A", out var receipt));
        fixture.Manager.RevokeRelay(fixture.Remote);
        Assert.False(fixture.Manager.IsCurrent(receipt));
        var replacement = CreatePeer(2);
        fixture.Players.SetPeer("B", replacement);
        Assert.True(fixture.Manager.TryEnterMission(replacement, "B", "battle", out _));
        Assert.False(fixture.Manager.IsCurrent(receipt));
        Assert.True(fixture.Manager.TryCaptureLocalDelivery(replacement, "battle", "A", out var fresh));
        Assert.True(fresh.SourceGeneration > receipt.SourceGeneration);
        Assert.True(fixture.Manager.IsCurrent(fresh));
    }

    private sealed class ForeignReceipt : MissionRelayReceipt
    {
        public ForeignReceipt(MissionRelayReceipt source) : base(source.Peer, source.ControllerId,
            source.InstanceId, source.TargetControllerId, source.SourceCredential, source.TargetCredential,
            source.SourceGeneration, source.TargetGeneration) { }
    }

    private sealed class Fixture
    {
        public readonly PlayerManager Players;
        public readonly MissionManager Manager;
        public readonly NetPeer Remote = CreatePeer(1);
        public Fixture()
        {
            var controller = new ControllerIdProvider(); controller.SetControllerId("A");
            var objects = new ObjectManager(Mock.Of<ILogger>());
            Players = new PlayerManager(Mock.Of<ILogger>(), objects, controller);
            Assert.True(Players.AddPlayer(new Player("A", "", "", "", "")));
            Assert.True(Players.AddPlayer(new Player("B", "", "", "", "")));
            Players.SetPeer("B", Remote);
            Manager = new MissionManager(Players, controller);
            Assert.True(Manager.TryEnterMission(new LocalPlayerEndpoint("A"), "battle", out _));
            Assert.True(Manager.TryEnterMission(Remote, "B", "battle", out _));
        }
    }

    private static NetPeer CreatePeer(int id) => (NetPeer)typeof(NetPeer).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(NetManager), typeof(IPEndPoint), typeof(int) }, null)!.Invoke(
            new object[] { new NetManager(null), new IPEndPoint(IPAddress.Loopback, 52910 + id), id });
}
