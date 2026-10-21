using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Interfaces;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class SnapshotApplyCompletionTests
{
    private static readonly AuthorityLease Source = new("room", "source", "b", 3);
    private static readonly AuthorityLease Paused = new("room", "paused", "", 4, true);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockOrMovementThrowRetainsFailureAndNeverConfirmsPausedLease(bool clockThrows)
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        Assert.True(identity.TryApply(Source, "a"));
        var checkpoint = new CampaignMovementCheckpoint(Source, 777, 1, new byte[] { 1 }, "hash");
        Func<bool> install = () => clockThrows ? true : throw new InvalidOperationException("movement failed");
        var movement = new Mock<ICampaignMovementCheckpointService>();
        movement.Setup(value => value.TryPrepare(checkpoint, Source, 777, out install)).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>();
        clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(777)).Returns(() => clockThrows
            ? throw new InvalidOperationException("clock failed") : true);
        var network = new Mock<INetwork>(); var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(network); var controller = new ControllerIdProvider(); controller.SetControllerId("a");
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, movement.Object, clock.Object,
            network.Object, controller, CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
        broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint, missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(Paused)));
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(Source, identity.CurrentLease);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(Paused, out var reason));
        Assert.Contains(clockThrows ? "clock failed" : "movement failed", reason);
        broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint, missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(Paused)));
        GameThread.Run(() => { }, blocking: true);
        clock.Verify(value => value.TryApplyAuthorityHandoffBaseline(777), Times.Once());
        network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never());
    }
    [Fact]
    public void SnapshotWaitsForActualDescendantsAndRedeliveryDoesNotInstallTwice()
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity(); Assert.True(identity.TryApply(Source, "a"));
        var checkpoint = new CampaignMovementCheckpoint(Source, 777, 1, new byte[] { 1 }, "hash");
        int installs = 0; bool descendant = false; bool descendantSawSource = false;
        Func<bool> install = () =>
        {
            Assert.Equal(CampaignExecutionPhase.CheckpointInstall, CampaignExecutionScope.Current);
            installs++;
            GameThread.EnqueueSafe(() =>
            {
                descendantSawSource = identity.CurrentLease == Source;
                GameThread.EnqueueSafe(() => descendant = true);
            });
            return true;
        };
        var movement = new Mock<ICampaignMovementCheckpointService>(); movement.Setup(value => value.TryPrepare(checkpoint, Source, 777, out install)).Returns(true);
        var clock = new Mock<IMapTimeTrackerInterface>(); clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(777)).Returns(true);
        var network = new Mock<INetwork>(); var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(network); var controller = new ControllerIdProvider(); controller.SetControllerId("a");
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, movement.Object, clock.Object,
            network.Object, controller, CampaignHandoffTestServices.Drained(), missionFacts: CampaignHandoffTestServices.MissionFacts(identity));
        var snapshot = new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint, missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(Paused));
        broker.Publish(roomPeer, snapshot);
        GameThread.Run(() => { }, blocking: true); GameThread.Run(() => { }, blocking: true); GameThread.Run(() => { }, blocking: true);
        Assert.True(descendant); Assert.True(descendantSawSource);
        GameThread.Run(() => { }, blocking: true);
        Assert.Equal(Paused, identity.CurrentLease); Assert.Equal(1, installs);
        network.Verify(value => value.SendAll(It.IsAny<NetworkCampaignMovementBaselineApplied>()), Times.Once());
        broker.Publish(roomPeer, snapshot); GameThread.Run(() => { }, blocking: true);
        Assert.Equal(1, installs);
        clock.Verify(value => value.TryApplyAuthorityHandoffBaseline(777), Times.Once());
        network.Verify(value => value.SendAll(It.IsAny<NetworkCampaignMovementBaselineApplied>()), Times.Once());
    }
    [Fact]
    public void InitialLeaseCannotPassUnconfirmedPendingApplyOrLaunderItsFailure()
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        var network = new Mock<INetwork>(); var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object, missionFacts: CampaignHandoffTestServices.MissionFacts(Source.RoomId));
        bool descendantSawUnconfirmed = false;
        GameThread.Run(() =>
        {
            using (CampaignApplyCompletion.For(identity).Observe(typeof(SnapshotApplyCompletionTests)))
                GameThread.EnqueueSafe(() => GameThread.EnqueueSafe(() => descendantSawUnconfirmed = !identity.IsConfirmed));
            broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(Source, "a", missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(Source)));
        }, blocking: true);
        GameThread.Run(() => { }, blocking: true); GameThread.Run(() => { }, blocking: true);
        GameThread.Run(() => { }, blocking: true);
        Assert.True(descendantSawUnconfirmed);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(default, out _));
        Assert.True(identity.IsConfirmed); Assert.Equal(Source, identity.CurrentLease);
    }
    [Fact]
    public void InitialLeaseCannotLaunderAFailedUnconfirmedApply()
    {
        using var broker = new MessageBroker(); var identity = new SessionIdentity();
        var network = new Mock<INetwork>(); var roomPeer = CampaignHandoffTestServices.AuthenticateRoomPeer(network);
        using var handler = new AuthorityLeaseSnapshotHandler(broker, identity, network: network.Object, missionFacts: CampaignHandoffTestServices.MissionFacts(Source.RoomId));
        CampaignApplyCompletion.For(identity).RecordFailure(default, "initial graph apply failed");
        broker.Publish(roomPeer, new NetworkAuthorityLeaseSnapshot(Source, "a", missionFactsVersion: CampaignHandoffTestServices.MissionFactsVersion(Source)));
        GameThread.Run(() => { }, blocking: true);
        Assert.False(identity.IsConfirmed);
        Assert.False(CampaignApplyCompletion.For(identity).CanAcknowledgeThrough(Source, out var reason));
        Assert.Contains("initial graph apply failed", reason);
    }
}
