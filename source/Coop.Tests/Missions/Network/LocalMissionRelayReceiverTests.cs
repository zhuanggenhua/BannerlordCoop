using Common;
using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Instances.Relay;
using Coop.Tests.Missions.Battles;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Surrogates;
using LiteNetLib;
using Missions;
using Missions.Agents;
using Missions.Agents.Handlers;
using Missions.Agents.Packets;
using Missions.Messages;
using Missions.Services.Network;
using Moq;
using Serilog;
using System;
using System.Net;
using System.Linq;
using FormatterServices = System.Runtime.Serialization.FormatterServices;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Coop.Tests.Missions.Network;

[Collection("Mission.Current")]
public class LocalMissionRelayReceiverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedMovementUsesActualPeerAndExistingMovementHandler(bool mount)
    {
        using var fixture = new Fixture();
        using var mission = new MissionCurrentScope();
        using var movement = fixture.CreateMovement();
        fixture.Decode(mount ? new MountMovementPacket(new[] { Guid.NewGuid() }, new AgentMountData[1], "B")
            : new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B"));
        Assert.True(fixture.Receiver.TryReceive(fixture.Receipt(), new byte[] { 1 }));
        fixture.Drain();
        fixture.Registry.Verify(value => value.TryGetAgentInfo(It.IsAny<Guid>(), out It.Ref<CoopAgentInfo>.IsAny), Times.Once());
        Assert.False(fixture.Context.TryGetPeer("B", out _)); // Campaign peer was never mapped as a direct Mission peer.
        Assert.False(fixture.Receiver.TryCapture(fixture.Peer, "B", out _)); // Ambient qualification cannot escape dispatch.
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("scene")]
    [InlineData("dispose")]
    public void AdmissionDoesNotCertifyApplyAndQueuedInvalidationDropsDelivery(string invalidation)
    {
        using var fixture = new Fixture();
        var packets = new Mock<IPacketManager>();
        using var receiver = new LocalMissionRelayReceiver(fixture.Manager, fixture.Context, fixture.Serializer.Object, packets.Object, new MovementPacketCompressor(fixture.Serializer.Object));
        fixture.Decode(new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B"));
        GameThread.Run(() =>
        {
            Assert.True(receiver.TryReceive(fixture.Receipt(), new byte[] { 1 }));
            if (invalidation == "source")
            {
                Assert.True(fixture.Manager.TryLeaveMission(fixture.Peer, "B", "battle", out _));
                Assert.True(fixture.Manager.TryEnterMission(fixture.Peer, "B", "battle", out _));
            }
            else if (invalidation == "target")
            {
                Assert.True(fixture.Manager.TryLeaveMission(new LocalPlayerEndpoint("A"), "battle", out _));
                Assert.True(fixture.Manager.TryEnterMission(new LocalPlayerEndpoint("A"), "battle", out _));
            }
            else if (invalidation == "scene")
            {
                fixture.Context.EndInstance();
                fixture.Context.BeginInstance("battle");
            }
            else receiver.Dispose();
        }, blocking: true);
        fixture.Drain();
        packets.Verify(value => value.HandleReceive(It.IsAny<NetPeer>(), It.IsAny<IPacket>()), Times.Never());
    }

    [Fact]
    public void ReceiptSurvivesOnlyExplicitContinuationAndRevalidatesMembershipAndScene()
    {
        using var fixture = new Fixture();
        var packets = new Mock<IPacketManager>();
        using var receiver = new LocalMissionRelayReceiver(fixture.Manager, fixture.Context, fixture.Serializer.Object, packets.Object, new MovementPacketCompressor(fixture.Serializer.Object));
        MissionPacketSourceReceipt captured = null;
        bool wrongPeer = true, wrongSender = true;
        packets.Setup(value => value.HandleReceive(fixture.Peer, It.IsAny<IPacket>())).Callback(() =>
        {
            Assert.True(receiver.TryCapture(fixture.Peer, "B", out captured));
            Assert.True(receiver.IsCurrent(captured));
            wrongPeer = receiver.TryCapture(CreatePeer(2), "B", out _);
            wrongSender = receiver.TryCapture(fixture.Peer, "A", out _);
        });
        fixture.Decode(new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B"));
        Assert.True(receiver.TryReceive(fixture.Receipt(), new byte[] { 1 }));
        fixture.Drain();
        Assert.NotNull(captured);
        Assert.Same(fixture.Peer, captured.Peer);
        Assert.False(wrongPeer); Assert.False(wrongSender);
        Assert.False(receiver.TryCapture(fixture.Peer, "B", out _));
        Assert.True(receiver.IsCurrent(captured));
        fixture.Context.EndInstance(); fixture.Context.BeginInstance("battle");
        Assert.False(receiver.IsCurrent(captured));
        Assert.False(fixture.Receiver.IsCurrent(captured)); // Receipt from another receiver cannot be reused.
    }

    [Fact]
    public void SpoofedInnerSenderUnsupportedReliableAndDecodeFailureNeverDispatch()
    {
        using var fixture = new Fixture();
        var packets = new Mock<IPacketManager>();
        using var receiver = new LocalMissionRelayReceiver(fixture.Manager, fixture.Context, fixture.Serializer.Object, packets.Object, new MovementPacketCompressor(fixture.Serializer.Object));
        var receipt = fixture.Receipt();
        fixture.Decode(new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "A"));
        Assert.False(receiver.TryReceive(receipt, new byte[] { 1 }));
        fixture.Decode(new NetworkMissionPeerEntered("B", "battle"));
        Assert.False(receiver.TryReceive(receipt, new byte[] { 1 }));
        fixture.Serializer.Setup(value => value.Deserialize(It.IsAny<byte[]>())).Throws(new InvalidOperationException("corrupt payload"));
        Assert.False(receiver.TryReceive(receipt, new byte[] { 1 }));
        Assert.False(receiver.TryReceive(null, new byte[] { 1 }));
        fixture.Drain();
        packets.Verify(value => value.HandleReceive(It.IsAny<NetPeer>(), It.IsAny<IPacket>()), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalDirectAndRemoteRoomRelayStillQualifyWithoutLocalReceipt(bool relay)
    {
        using var fixture = new Fixture();
        using var mission = new MissionCurrentScope();
        var room = new Mock<IRelayNetwork>();
        if (relay) room.SetupGet(value => value.ServerEndpoint).Returns(new IPEndPoint(fixture.Peer.Address, fixture.Peer.Port));
        else fixture.Context.MapPeer("B", fixture.Peer);
        using var movement = fixture.CreateMovement(room.Object, useLocalPolicy: false);
        movement.HandlePacket(fixture.Peer, new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B"));
        fixture.Drain();
        fixture.Registry.Verify(value => value.TryGetAgentInfo(It.IsAny<Guid>(), out It.Ref<CoopAgentInfo>.IsAny), Times.Once());
    }

    [Fact]
    public void UnqualifiedCampaignPeerAndSpoofedDirectPeerCannotReachAgentLookup()
    {
        using var fixture = new Fixture();
        using var mission = new MissionCurrentScope();
        using var movement = fixture.CreateMovement();
        movement.HandlePacket(fixture.Peer, new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B"));
        fixture.Context.MapPeer("B", fixture.Peer);
        movement.HandlePacket(fixture.Peer, new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "A"));
        fixture.Drain();
        fixture.Registry.Verify(value => value.TryGetAgentInfo(It.IsAny<Guid>(), out It.Ref<CoopAgentInfo>.IsAny), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCompressedRelayRoundTripsThroughExistingSerializerCompressorAndMovementHandler(bool mount)
    {
        using var fixture = new Fixture();
        using var mission = new MissionCurrentScope();
        _ = new SurrogateCollection();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var compressor = new MovementPacketCompressor(serializer);
        using var receiver = new LocalMissionRelayReceiver(fixture.Manager, fixture.Context, serializer, fixture.Packets, compressor);
        using var movement = fixture.CreateMovement(policy: receiver);
        var relay = new RelayPacketHandler(fixture.Packets, fixture.Manager, receiver, fixture.Manager);
        try
        {
            // AgentMountData normally captures a native Agent; this managed codec test supplies
            // non-null zero-state DTOs via the same constructor-skipping model as Protobuf.
            var mounts = Enumerable.Range(0, 8).Select(_ =>
                (AgentMountData)FormatterServices.GetUninitializedObject(typeof(AgentMountData))).ToArray();
            IPacket inner = mount ? new MountMovementPacket(new Guid[8], mounts, "B")
                : new MovementPacket(new Guid[8], new AgentData[8], "B");
            byte[] payload = compressor.Serialize(inner);
            Assert.IsType<CompressedMovementPacket>(serializer.Deserialize(payload));
            relay.HandlePacket(fixture.Peer, new RelayPacket(DeliveryMethod.Unreliable, "battle", "A", payload));
            fixture.Drain();
            fixture.Registry.Verify(value => value.TryGetAgentInfo(It.IsAny<Guid>(), out It.Ref<CoopAgentInfo>.IsAny), Times.Exactly(8));
            Assert.False(receiver.TryReceive(fixture.Receipt(), compressor.Serialize(
                new MovementPacket(new Guid[8], new AgentData[8], "A"))));
            Assert.False(receiver.TryReceive(fixture.Receipt(), serializer.Serialize(new CompressedMovementPacket(99, new byte[] { 1 }))));
            // Existing Protobuf intentionally cannot encode a nested IPacket wrapper. Exercise the
            // receiver type gate with a decoded wrapper without changing that serializer contract.
            fixture.Decode(new PacketWrapper(inner));
            Assert.False(fixture.Receiver.TryReceive(fixture.Receipt(), new byte[] { 1 }));
        }
        finally { relay.Dispose(); }
    }

    [Fact]
    public void MovementApplyRevalidatesExplicitReceiptAfterSourceLeaveAndRejoin()
    {
        using var fixture = new Fixture();
        using var mission = new MissionCurrentScope();
        var packets = new Mock<IPacketManager>();
        using var receiver = new LocalMissionRelayReceiver(fixture.Manager, fixture.Context, fixture.Serializer.Object, packets.Object,
            new MovementPacketCompressor(fixture.Serializer.Object));
        MissionPacketSourceReceipt captured = null;
        packets.Setup(value => value.HandleReceive(fixture.Peer, It.IsAny<IPacket>())).Callback(() =>
            Assert.True(receiver.TryCapture(fixture.Peer, "B", out captured)));
        var packet = new MovementPacket(new[] { Guid.NewGuid() }, new AgentData[1], "B");
        fixture.Decode(packet);
        Assert.True(receiver.TryReceive(fixture.Receipt(), new byte[] { 1 })); fixture.Drain();
        Assert.NotNull(captured);
        var policy = new Mock<IMissionPacketSourcePolicy>();
        policy.Setup(value => value.TryCapture(fixture.Peer, "B", out captured)).Returns(true);
        policy.Setup(value => value.IsCurrent(captured)).Returns(() => receiver.IsCurrent(captured));
        using var movement = fixture.CreateMovement(policy: policy.Object);
        Exception producerFailure = null;
        GameThread.Run(() =>
        {
            // The producer queues the original movement apply while this game-thread action holds the drain.
            var producer = new Thread(() => { try { movement.HandlePacket(fixture.Peer, packet); } catch (Exception error) { producerFailure = error; } });
            producer.Start(); Assert.True(producer.Join(TimeSpan.FromSeconds(5))); Assert.Null(producerFailure);
            Assert.True(fixture.Manager.TryLeaveMission(fixture.Peer, "B", "battle", out _));
            Assert.True(fixture.Manager.TryEnterMission(fixture.Peer, "B", "battle", out _));
        }, blocking: true);
        fixture.Drain();
        policy.Verify(value => value.IsCurrent(captured), Times.Once());
        fixture.Registry.Verify(value => value.TryGetAgentInfo(It.IsAny<Guid>(), out It.Ref<CoopAgentInfo>.IsAny), Times.Never());
    }

    private sealed class Fixture : IDisposable
    {
        public readonly MessageBroker Broker = new();
        public readonly ControllerIdProvider Controller = new();
        public readonly NetPeer Peer = CreatePeer(1);
        public readonly PlayerManager Players;
        public readonly MissionManager Manager;
        public readonly MissionContext Context;
        public readonly Mock<ICommonSerializer> Serializer = new();
        public readonly PacketManager Packets = new();
        public readonly Mock<INetworkAgentRegistry> Registry = new();
        public readonly LocalMissionRelayReceiver Receiver;
        public Fixture()
        {
            Controller.SetControllerId("A");
            Players = new PlayerManager(Mock.Of<ILogger>(), new ObjectManager(Mock.Of<ILogger>()), Controller);
            Assert.True(Players.AddPlayer(new Player("A", "", "", "", "")));
            Assert.True(Players.AddPlayer(new Player("B", "", "", "", "")));
            Players.SetPeer("B", Peer);
            Manager = new MissionManager(Players, Controller);
            Assert.True(Manager.TryEnterMission(new LocalPlayerEndpoint("A"), "battle", out _));
            Assert.True(Manager.TryEnterMission(Peer, "B", "battle", out _));
            Context = new MissionContext(Broker, Controller); Context.BeginInstance("battle");
            Broker.Publish(this, new NetworkMissionPeerEntered("B", "battle"));
            Receiver = new LocalMissionRelayReceiver(Manager, Context, Serializer.Object, Packets, new MovementPacketCompressor(Serializer.Object));
        }
        public void Decode(object decoded) => Serializer.Setup(value => value.Deserialize(It.IsAny<byte[]>())).Returns(decoded);
        public MissionRelayReceipt Receipt()
        {
            Assert.True(Manager.TryCaptureLocalDelivery(Peer, "battle", "A", out var receipt));
            return receipt;
        }
        public AgentMovementHandler CreateMovement(IRelayNetwork relay = null, bool useLocalPolicy = true, IMissionPacketSourcePolicy policy = null) => new(
            Mock.Of<IBattleNetwork>(), Packets, Broker, Registry.Object, Controller,
            Mock.Of<IAgentEquipmentApplier>(), Mock.Of<IMovementBatchSender>(), Mock.Of<IPuppetMountStateRepairer>(),
            Mock.Of<IAgentVisualActionAccessor>(), Mock.Of<IMovementRateController>(), Mock.Of<IMovementPriorityScheduler>(),
            Context, relay, policy ?? (useLocalPolicy ? Receiver : null));
        public void Drain() { for (int i = 0; i < 3; i++) GameThread.Run(() => { }, blocking: true); }
        public void Dispose() { Receiver.Dispose(); Context.Dispose(); Broker.Dispose(); }
    }

    private static NetPeer CreatePeer(int id) => (NetPeer)typeof(NetPeer).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(NetManager), typeof(IPEndPoint), typeof(int) }, null)!.Invoke(
            new object[] { new NetManager(null), new IPEndPoint(IPAddress.Loopback, 52940 + id), id });
}
