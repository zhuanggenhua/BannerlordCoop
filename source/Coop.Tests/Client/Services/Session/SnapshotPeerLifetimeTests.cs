using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Client.Services.Session;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Interfaces;
using LiteNetLib;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Client.Services.Session;

public class SnapshotPeerLifetimeTests
{
    private sealed class Fixture : IDisposable
    {
        internal static readonly AuthorityLease Source = new("room", "source", "b", 3);
        internal static readonly AuthorityLease Paused = new("room", "paused", "", 4, true);
        internal readonly MessageBroker Broker = new();
        internal readonly SessionIdentity Identity = new();
        internal readonly Mock<INetwork> Network = new();
        internal readonly AuthorityLeaseSnapshotHandler Handler;
        internal readonly NetPeer InitialPeer = new TestNetwork().CreatePeer();
        internal NetPeer CurrentPeer;
        internal int ClockCalls, Installs;
        internal Action DuringInstall;
        internal readonly NetworkAuthorityLeaseSnapshot Snapshot;
        internal Fixture()
        {
            Assert.True(Identity.TryApply(Source, "a")); CurrentPeer = InitialPeer;
            Network.As<IRoomHostPeerProvider>().Setup(value => value.IsCurrentRoomHostPeer(It.IsAny<NetPeer>()))
                .Returns((NetPeer peer) => ReferenceEquals(peer, CurrentPeer));
            var checkpoint = new CampaignMovementCheckpoint(Source, 777, 1, new byte[] { 1 }, "hash");
            Func<bool> install = () => { Installs++; DuringInstall?.Invoke(); return true; };
            var movement = new Mock<ICampaignMovementCheckpointService>();
            movement.Setup(value => value.TryPrepare(checkpoint, Source, 777, out install)).Returns(true);
            var clock = new Mock<IMapTimeTrackerInterface>();
            clock.Setup(value => value.TryApplyAuthorityHandoffBaseline(777)).Callback(() => ClockCalls++).Returns(true);
            var controller = new ControllerIdProvider(); controller.SetControllerId("a");
            Handler = new AuthorityLeaseSnapshotHandler(Broker, Identity, movement.Object, clock.Object,
                Network.Object, controller, CampaignHandoffTestServices.Drained());
            Snapshot = new NetworkAuthorityLeaseSnapshot(Paused, "a", checkpoint);
        }
        internal void Publish() => Broker.Publish(InitialPeer, Snapshot);
        internal void Drain() { for (int i = 0; i < 4; i++) GameThread.Run(() => { }, blocking: true); }
        internal void NoReceipt() => Network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never());
        public void Dispose() { Handler.Dispose(); Broker.Dispose(); }
    }
    [Fact]
    public void QueuedSnapshotFromReplacedPeerCannotInstallDespiteUnchangedIdentityStrings()
    {
        using var fixture = new Fixture();
        GameThread.Run(() => { fixture.Publish(); fixture.CurrentPeer = new TestNetwork().CreatePeer(); }, blocking: true);
        fixture.Drain();
        Assert.Equal(0, fixture.ClockCalls); Assert.Equal(0, fixture.Installs);
        Assert.Equal(Fixture.Source, fixture.Identity.CurrentLease); fixture.NoReceipt();
    }
    [Fact]
    public void AlreadyQueuedInitialSnapshotCannotRunAfterHandlerDisposal()
    {
        using var fixture = new Fixture();
        GameThread.Run(() => { fixture.Publish(); fixture.Handler.Dispose(); }, blocking: true);
        fixture.Drain();
        Assert.Equal(0, fixture.ClockCalls); Assert.Equal(0, fixture.Installs);
        Assert.Equal(Fixture.Source, fixture.Identity.CurrentLease); fixture.NoReceipt();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredSnapshotCannotConfirmOrAcknowledgeAfterPeerReplacementOrDisposal(bool dispose)
    {
        using var fixture = new Fixture();
        fixture.DuringInstall = () => GameThread.EnqueueSafe(() =>
        {
            if (dispose) fixture.Handler.Dispose();
            else fixture.CurrentPeer = new TestNetwork().CreatePeer();
        });
        fixture.Publish(); fixture.Drain();
        Assert.Equal(1, fixture.ClockCalls); Assert.Equal(1, fixture.Installs);
        Assert.Equal(Fixture.Source, fixture.Identity.CurrentLease); fixture.NoReceipt();
        if (!dispose) Assert.Equal(CampaignApplyStatus.Faulted,
            CampaignApplyCompletion.For(fixture.Identity).GetStatusThrough(Fixture.Paused, out _));
    }
}
