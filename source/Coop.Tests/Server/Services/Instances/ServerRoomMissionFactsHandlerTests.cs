using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Coop.Core.Client;
using Coop.Core.Client.Services.Instances;
using Coop.Core.Server;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Instances.Handlers;
using Coop.Tests.Mocks;
using GameInterface;
using GameInterface.Services.Missions;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using Xunit;

namespace Coop.Tests.Server.Services.Instances;

[Collection(ModInformationRoleCollection.Name)]
public class ServerRoomMissionFactsHandlerTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly MessageBroker Broker = new();
        internal readonly SessionIdentity Session = new();
        internal readonly Mock<INetwork> Network = new();
        internal readonly Mock<IPlayerManager> Players = new();
        internal readonly NetPeer Peer = new TestNetwork().CreatePeer();
        internal readonly MissionManager Source;
        internal readonly ServerRoomMissionFactsHandler Handler;

        internal Fixture()
        {
            Assert.True(Session.TryApply(new AuthorityLease("room", "authority", "A", 1), "A"));
            var player = new Player("B", "hero-b", "party-b", "clan-b", "character-b");
            Players.Setup(value => value.TryGetPlayer(Peer, out player)).Returns(true);
            var peer = Peer;
            Players.Setup(value => value.TryGetPeer("B", out peer)).Returns(true);
            Source = new MissionManager(Players.Object, null, Session);
            Handler = new ServerRoomMissionFactsHandler(Broker, Network.Object, Source, Players.Object);
        }

        internal void Drain() { for (int i = 0; i < 3; i++) GameThread.Run(() => { }, blocking: true); }
        public void Dispose() { Handler.Dispose(); Broker.Dispose(); }
    }

    [Fact]
    public void PostCatchupSendsExplicitCompleteSnapshotEvenWhenTheNativeSaveHadNoRoomMembership()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Source.TryEnterMission(fixture.Peer, "B", "town", out _));
        Assert.True(fixture.Source.TryCapture(out var expected));
        fixture.Broker.Publish(this, new PlayerCampaignSynchronized(fixture.Peer));
        fixture.Drain();
        fixture.Network.Verify(value => value.SendImmediate(fixture.Peer,
            It.Is<NetworkRoomMissionFactsSnapshot>(message => message.Snapshot.IsValid &&
                message.Snapshot.Version == expected.Version && message.Snapshot.Members.Length == 1)), Times.Once);
    }

    [Fact]
    public void AckOnlyCountsAfterGameThreadApplyAndIsInvalidatedByMembershipChanges()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Source.TryCapture(out var empty));
        GameThread.Run(() =>
        {
            fixture.Broker.Publish(fixture.Peer, new NetworkRoomMissionFactsApplied(empty.Version));
            Assert.False(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
        }, blocking: true);
        fixture.Drain();
        Assert.True(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
        Assert.True(fixture.Source.TryEnterMission(fixture.Peer, "B", "town", out _));
        Assert.False(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
        Assert.True(fixture.Source.TryCapture(out var entered));
        Assert.False(fixture.Handler.HasApplied(fixture.Peer, entered.Version));
        fixture.Broker.Publish(fixture.Peer, new NetworkRoomMissionFactsApplied(entered.Version));
        fixture.Drain();
        Assert.True(fixture.Handler.HasApplied(fixture.Peer, entered.Version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedAckCannotCrossPeerReplacementOrHandlerDisposal(bool dispose)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Source.TryCapture(out var empty));
        GameThread.Run(() =>
        {
            fixture.Broker.Publish(fixture.Peer, new NetworkRoomMissionFactsApplied(empty.Version));
            if (dispose) fixture.Handler.Dispose();
            else
            {
                var next = new TestNetwork().CreatePeer();
                fixture.Players.Setup(value => value.TryGetPeer("B", out next)).Returns(true);
            }
        }, blocking: true);
        fixture.Drain();
        Assert.False(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
    }

    [Fact]
    public void DisconnectClearsReceiptWithoutRemovingAnotherPeersIdentity()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Source.TryCapture(out var empty));
        fixture.Broker.Publish(fixture.Peer, new NetworkRoomMissionFactsApplied(empty.Version));
        fixture.Drain();
        Assert.True(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
        fixture.Broker.Publish(this, new PlayerDisconnected(fixture.Peer, default));
        Assert.False(fixture.Handler.HasApplied(fixture.Peer, empty.Version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionContainerUsesOneFactsInstanceForAllMembershipQueries(bool server)
    {
        var builder = new ContainerBuilder();
        if (server) builder.RegisterModule<ServerModule>();
        else builder.RegisterModule<ClientModule>();
        builder.RegisterModule<GameInterfaceModule>();
        using var container = builder.Build();
        Assert.Same(container.Resolve<IMissionMembershipRegistry>(), container.Resolve<IRoomMissionFactsView>());
        if (server)
        {
            Assert.Same(container.Resolve<IMissionManager>(), container.Resolve<IRoomMissionFactsView>());
            Assert.Same(container.Resolve<ServerRoomMissionFactsHandler>(), container.Resolve<IRoomMissionFactsPublisher>());
        }
        else Assert.Same(container.Resolve<RoomMissionMembershipProjection>(), container.Resolve<IRoomMissionFactsView>());
    }
}
