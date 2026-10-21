using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.Entity;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MapEvents.TroopSupply.Handlers;
using GameInterface.Services.MapEvents.TroopSupply.Messages;
using GameInterface.Services.Missions;
using Moq;
using System;
using TaleWorlds.Core;
using Xunit;

namespace Coop.Tests.Missions.Battles;

[Collection(ModInformationRoleCollection.Name)]
public class LocalMissionReserveConsumerTests
{
    [Fact]
    public void CurrentLocalRoomReplyBuffersOriginalReserveUntilSupplierRegisters()
    {
        using var fixture = new Fixture();
        fixture.Broker.Publish(fixture.Source(), fixture.Reserve());
        var supplier = fixture.RegisterSupplier();
        Assert.True(supplier.IsPopulated);
        Assert.Equal(17, supplier.AllocationRevision);
        Assert.Equal("party", Assert.Single(supplier.GetSuppliedByParty()).partyId);
        fixture.AssertNoFlush();
    }

    [Fact]
    public void LocalReplaceFlushCapturesDroppedPointersAndReturnsOnlyToRoomControl()
    {
        using var fixture = new Fixture();
        var supplier = fixture.RegisterSupplier();
        fixture.Broker.Publish(fixture.Source(), fixture.Reserve(supplied: 1));
        fixture.Broker.Publish(fixture.Source(), fixture.Reserve(supplied: 0));
        Assert.Equal(1, Assert.Single(supplier.GetSuppliedByParty()).supplied);
        var refresh = new NetworkBattleTroopReserve(fixture.Battle, 1, Array.Empty<PartyReserve>(), 0, 0, 18, 200, true);
        fixture.Broker.Publish(fixture.Source(), refresh);
        fixture.Broker.Publish(fixture.Source(), refresh);
        Assert.Empty(supplier.GetSuppliedByParty());
        Assert.Equal(18, supplier.AllocationRevision);
        fixture.Room.Verify(value => value.SendAll(It.Is<NetworkBattleSupplyProgress>(message =>
            message.IsFlush && message.MapEventId == fixture.Battle && message.Entries.Length == 1 &&
            message.Entries[0].PartyId == "party" && message.Entries[0].SuppliedCount == 1)), Times.Once);
        fixture.Room.Verify(value => value.SendAll(It.Is<NetworkBattleSupplyProgress>(message =>
            message.IsFlush && message.Entries.Length == 0)), Times.Once);
        fixture.Network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("room")]
    [InlineData("host")]
    [InlineData("session")]
    [InlineData("actor")]
    [InlineData("peer")]
    [InlineData("missing-room-adapter")]
    public void InvalidLocalControlCannotFeedOrAcknowledgeReserve(string invalidation)
    {
        using var fixture = new Fixture(registerRoom: invalidation != "missing-room-adapter");
        object source = fixture.Source();
        if (invalidation == "target") source = new LocalMissionControlEndpoint(fixture.Session, "B");
        if (invalidation == "room") source = new LocalMissionControlEndpoint(Fixture.SessionForRoom("next-room"), "A");
        if (invalidation == "host") source = new LocalMissionControlEndpoint(Fixture.SessionForRoom(host: "B"), "A");
        if (invalidation == "session") source = new LocalMissionControlEndpoint(Fixture.SessionForRoom(), "A");
        if (invalidation == "actor") source = new LocalPlayerEndpoint("A");
        if (invalidation == "peer") source = new Coop.Tests.Mocks.TestNetwork().CreatePeer();
        var supplier = fixture.RegisterSupplier();
        fixture.Broker.Publish(source, fixture.Reserve(flush: true));
        Assert.False(supplier.IsPopulated);
        fixture.AssertNoFlush();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalControlRevalidatesCapturedRoomAndHostOnTheSameSessionBinding(bool changeRoom)
    {
        var identity = new Mock<ISessionIdentity>();
        identity.SetupGet(value => value.IsConfirmed).Returns(true);
        identity.SetupGet(value => value.RoomId).Returns("room");
        identity.SetupGet(value => value.RoomHostControllerId).Returns("A");
        var source = new LocalMissionControlEndpoint(identity.Object, "A");
        Assert.True(source.IsCurrent(identity.Object, "A"));
        if (changeRoom) identity.SetupGet(value => value.RoomId).Returns("next-room");
        else identity.SetupGet(value => value.RoomHostControllerId).Returns("B");
        Assert.False(source.IsCurrent(identity.Object, "A"));
    }

    [Fact]
    public void MissionRoomControlRemainsCurrentAcrossWorldHandoffAndGlobalPause()
    {
        using var fixture = new Fixture();
        var source = fixture.Source();
        Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "writer-b", "B", 2), "A"));
        Assert.True(source.IsCurrent(fixture.Session, "A"));
        Assert.True(fixture.Session.TryApply(new AuthorityLease("room", "paused", "", 3, true), "A"));
        var supplier = fixture.RegisterSupplier();
        fixture.Broker.Publish(source, fixture.Reserve());
        Assert.True(supplier.IsPopulated);
    }

    [Fact]
    public void MissingLocalSessionCannotConsumeOrFlushReserve()
    {
        using var fixture = new Fixture(registerSession: false);
        var supplier = fixture.RegisterSupplier();
        fixture.Broker.Publish(fixture.Source(), fixture.Reserve(flush: true));
        Assert.False(supplier.IsPopulated);
        fixture.AssertNoFlush();
    }

    [Fact]
    public void DedicatedServerDoesNotConsumeLocalPlayerReserves()
    {
        using var fixture = new Fixture();
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        var supplier = fixture.RegisterSupplier();
        fixture.Broker.Publish(fixture.Source(), fixture.Reserve(flush: true));
        Assert.False(supplier.IsPopulated);
        fixture.AssertNoFlush();
    }

    [Fact]
    public void RemoteReplicaRetainsOriginalReserveAndCampaignPeerFlushPath()
    {
        using var fixture = new Fixture();
        ModInformation.SessionMode = SessionMode.RemoteReplica;
        var supplier = fixture.RegisterSupplier();
        var peer = new Coop.Tests.Mocks.TestNetwork().CreatePeer();
        fixture.Broker.Publish(peer, fixture.Reserve(flush: true));
        Assert.True(supplier.IsPopulated);
        fixture.Network.Verify(value => value.SendAll(It.Is<NetworkBattleSupplyProgress>(message =>
            message.IsFlush && message.MapEventId == fixture.Battle)), Times.Once);
        fixture.Room.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previous = ModInformation.SessionMode;
        private readonly IContainer container;
        public readonly string Battle = Guid.NewGuid().ToString("N");
        public readonly MessageBroker Broker = new();
        public readonly Mock<INetwork> Network = new();
        public readonly Mock<IRelayNetwork> Room = new();
        public readonly SessionIdentity Session = SessionForRoom();
        private readonly ControllerIdProvider controller = new();

        public static SessionIdentity SessionForRoom(string room = "room", string host = "A")
        {
            var session = new SessionIdentity();
            Assert.True(session.TryApply(new AuthorityLease(room, "writer", "A", 1), host));
            return session;
        }

        public Fixture(bool registerRoom = true, bool registerSession = true)
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            controller.SetControllerId("A");
            var builder = new ContainerBuilder();
            builder.RegisterInstance(Broker).As<IMessageBroker>();
            builder.RegisterInstance(Network.Object).As<INetwork>();
            if (registerRoom) builder.RegisterInstance(Room.Object).As<IRelayNetwork>();
            if (registerSession) builder.RegisterInstance(Session).As<ISessionIdentity>();
            builder.RegisterInstance(controller).As<IControllerIdProvider>();
            builder.RegisterType<BattleTroopReserveHandler>();
            container = builder.Build();
            container.Resolve<BattleTroopReserveHandler>();
        }

        public LocalMissionControlEndpoint Source() => new(Session, "A");
        public NetworkBattleTroopReserve Reserve(int supplied = 0, bool flush = false) => new(Battle, 1,
            new[] { new PartyReserve("party", supplied, new[] { new TroopReserveEntry(11, "troop", 0) }) }, 1, 1, 17, 200, flush);
        public CoopTroopSupplier RegisterSupplier()
        {
            var supplier = new CoopTroopSupplier(Battle, BattleSideEnum.Attacker, null, Mock.Of<IBattleAgentBudget>());
            CoopTroopSupplierRegistry.Register(supplier);
            return supplier;
        }
        public void AssertNoFlush()
        {
            Network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
            Room.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
        }
        public void Dispose()
        {
            container.Dispose();
            CoopTroopSupplierRegistry.ClearBattle(Battle);
            ModInformation.SessionMode = previous;
        }
    }
}
