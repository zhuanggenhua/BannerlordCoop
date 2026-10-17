using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Messaging;
using Common.Network.Session;
using Common.Session;
using Coop.Tests.Extensions;
using Coop.Core.Common.Network;
using Coop.Core.Client.Services.TroopRosters.Messages;
using Coop.Core.Server.Services.MobileParties.Packets;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Surrogates;
using LiteNetLib;
using Moq;
using ProtoBuf;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Coop.Tests.Network;

public class CoopNetworkBaseTests
{
    [Fact]
    public void AuthorityPartyBehaviorDelta_IsSerializedInsideLeaseEnvelope()
    {
        _ = new SurrogateCollection();
        var config = Mock.Of<INetworkConfig>();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var batcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var cancellation = new CancellationTokenSource();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        using var network = new TestNetwork(config, serializer, batcher, cancellation, authorityContext.Object);
        var update = new NetworkPartyBehaviorUpdateData { MobilePartyId = 42, TargetPosition = default };

        network.SendAll(new NetworkCampaignAuthorityPartyBehaviorDelta(update));

        var envelope = DeserializeEnvelope(network.LastBroadcast, serializer);
        var delta = Assert.IsType<NetworkCampaignAuthorityPartyBehaviorDelta>(
            serializer.Deserialize<IMessage>(envelope.Payload));
        Assert.Equal("room-a", envelope.RoomId);
        Assert.Equal("authority-b", envelope.AuthorityId);
        Assert.Equal("controller-b", envelope.AuthorityControllerId);
        Assert.Equal(2, envelope.Epoch);
        Assert.Equal(1, envelope.Sequence);
        Assert.Equal(42u, delta.BehaviorUpdateData.MobilePartyId);
    }

    [Fact]
    public void RemoteAuthorityPartyBehaviorCommand_RoundTripsWithLeaseAndRequestSequence()
    {
        _ = new SurrogateCollection();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 2);
        var command = new NetworkCampaignAuthorityPartyBehaviorCommand(
            lease,
            sequence: 7,
            originControllerId: "controller-a",
            behaviorUpdateData: new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 });

        var roundTrip = Assert.IsType<NetworkCampaignAuthorityPartyBehaviorCommand>(
            serializer.Deserialize<IMessage>(serializer.Serialize(command)));

        Assert.True(roundTrip.Matches(lease));
        Assert.Equal(7, roundTrip.Sequence);
        Assert.Equal("controller-a", roundTrip.OriginControllerId);
        Assert.Equal(42u, roundTrip.BehaviorUpdateData.MobilePartyId);
        Assert.False(roundTrip.Matches(new AuthorityLease("room-a", "authority-old", "controller-b", 1)));
    }

    [Fact]
    public void PlayerPartyBehaviorRequest_RoundTripsWithItsCurrentLease()
    {
        _ = new SurrogateCollection();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var request = new RequestMobilePartyBehaviorPacket(
            new NetworkPartyBehaviorUpdateData { MobilePartyId = 42 },
            requestSequence: 11,
            lease: lease);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var roundTrip = Serializer.Deserialize<RequestMobilePartyBehaviorPacket>(stream);

        Assert.True(roundTrip.MatchesLease(lease));
        Assert.Equal(11, roundTrip.RequestSequence);
        Assert.Equal(42u, roundTrip.BehaviorUpdateData.MobilePartyId);
        Assert.False(roundTrip.MatchesLease(new AuthorityLease("room-a", "authority-b", "controller-b", 4)));
    }

    [Fact]
    public void RecruitmentRequestAndAuthorityCommand_RoundTripWithLeaseAndTroopSelection()
    {
        _ = new SurrogateCollection();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        var troops = new[] { new TroopInfo("hero-a", "troop-a", 2) };
        var request = new ClientRequestRecruitment("party-a", troops, requestSequence: 11, lease: lease);
        using var requestStream = new MemoryStream();
        Serializer.Serialize(requestStream, request);
        requestStream.Position = 0;
        var requestRoundTrip = Serializer.Deserialize<ClientRequestRecruitment>(requestStream);

        Assert.True(requestRoundTrip.MatchesLease(lease));
        Assert.Equal(11, requestRoundTrip.RequestSequence);
        Assert.Equal("party-a", requestRoundTrip.MobilePartyId);
        Assert.Equal("troop-a", Assert.Single(requestRoundTrip.TroopsInCart).CharacterObjectId);

        var command = new NetworkCampaignAuthorityRecruitmentCommand(
            lease, 3, "controller-a", "party-a", troops);
        using var commandStream = new MemoryStream();
        Serializer.Serialize(commandStream, command);
        commandStream.Position = 0;
        var commandRoundTrip = Serializer.Deserialize<NetworkCampaignAuthorityRecruitmentCommand>(commandStream);

        Assert.True(commandRoundTrip.Matches(lease));
        Assert.Equal(3, commandRoundTrip.Sequence);
        Assert.Equal("controller-a", commandRoundTrip.OriginControllerId);
        Assert.Equal("troop-a", Assert.Single(commandRoundTrip.TroopsInCart).CharacterObjectId);
    }

    [Fact]
    public void RecruitmentRosterAndVolunteerResultsUseAuthorityLeaseEnvelopes()
    {
        _ = new SurrogateCollection();
        var config = Mock.Of<INetworkConfig>();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var batcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var cancellation = new CancellationTokenSource();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-b", "controller-b", 5);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        using var network = new TestNetwork(config, serializer, batcher, cancellation, authorityContext.Object);

        network.SendAll(new NetworkTroopRosterElementBatch(
            rosterId: 7,
            characterId: 9,
            operations: new[] { TroopRosterElementOperation.AddCounts(1, 0, 0, removeDepleted: true) }));
        var rosterEnvelope = DeserializeEnvelope(network.LastBroadcast, serializer);
        var rosterDelta = Assert.IsType<NetworkTroopRosterElementBatch>(
            serializer.Deserialize<IMessage>(rosterEnvelope.Payload));
        Assert.Equal(lease.Epoch, rosterEnvelope.Epoch);
        Assert.Equal(7u, rosterDelta.RosterId);
        Assert.Equal(1, Assert.Single(rosterDelta.Operations).Count);

        network.SendAll(new UpdateVolunteers(new System.Collections.Generic.Dictionary<uint, uint[]>
        {
            [17] = new uint[] { 0, 42 },
        }));
        var volunteerEnvelope = DeserializeEnvelope(network.LastBroadcast, serializer);
        var volunteerDelta = Assert.IsType<UpdateVolunteers>(
            serializer.Deserialize<IMessage>(volunteerEnvelope.Payload));
        Assert.Equal(lease.Epoch, volunteerEnvelope.Epoch);
        Assert.Equal(new uint[] { 0, 42 }, volunteerDelta.UpdatedVolunteerTypeIds[17]);
    }

    [Fact]
    public void Constructor_UsesEstablishedDisconnectTimeout()
    {
        var config = new Mock<INetworkConfig>();
        config.SetupGet(value => value.ConnectionTimeout).Returns(TimeSpan.FromSeconds(10));
        config.SetupGet(value => value.DisconnectTimeout).Returns(TimeSpan.FromSeconds(60));
        config.SetupGet(value => value.NetworkPollInterval).Returns(TimeSpan.FromMilliseconds(25));
        config.SetupGet(value => value.UpdateTime).Returns(TimeSpan.FromMilliseconds(15));

        ICommonSerializer serializer = Mock.Of<ICommonSerializer>();
        var reliableMessageBatcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var sessionCancellation = new CancellationTokenSource();
        using var network = new TestNetwork(
            config.Object,
            serializer,
            reliableMessageBatcher,
            sessionCancellation);

        Assert.Equal(60_000, network.AppliedDisconnectTimeout);
    }

    [Fact]
    public void DiscardPendingMessages_RemovesOnlyTheAbortedPeersBufferedPayload()
    {
        var config = Mock.Of<INetworkConfig>();
        var serializer = Mock.Of<ICommonSerializer>();
        var batcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var cancellation = new CancellationTokenSource();
        using var network = new TestNetwork(config, serializer, batcher, cancellation);
        var peers = new Mocks.TestNetwork();
        var aborted = peers.CreatePeer();
        var live = peers.CreatePeer();
        live.Setup(live.Id, "127.0.0.2");
        var sent = new System.Collections.Generic.List<NetPeer>();
        batcher.Send(aborted, new byte[] { 1 }, (peer, _) => sent.Add(peer));
        batcher.Send(live, new byte[] { 2 }, (peer, _) => sent.Add(peer));
        Assert.Empty(sent);
        ((IBufferedNetwork)network).DiscardPendingMessages(aborted);
        batcher.FlushAll(_ => true, (peer, _) => sent.Add(peer));
        Assert.Same(live, Assert.Single(sent));
    }

    [Fact]
    public void AuthorityScopedMessages_AreWrappedWithLeaseEpochAndMonotonicSequence()
    {
        var config = Mock.Of<INetworkConfig>();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var batcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var cancellation = new CancellationTokenSource();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 4);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        using var network = new TestNetwork(config, serializer, batcher, cancellation, authorityContext.Object);

        network.SendAll(new AuthorityProbeMessage { Value = 11 });
        NetworkCampaignAuthoritySyncEnvelope first = DeserializeEnvelope(network.LastBroadcast, serializer);
        network.SendAll(new AuthorityProbeMessage { Value = 12 });
        NetworkCampaignAuthoritySyncEnvelope second = DeserializeEnvelope(network.LastBroadcast, serializer);

        Assert.Equal("room-a", first.RoomId);
        Assert.Equal("authority-a", first.AuthorityId);
        Assert.Equal("controller-a", first.AuthorityControllerId);
        Assert.Equal(4, first.Epoch);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(11, Assert.IsType<AuthorityProbeMessage>(serializer.Deserialize<IMessage>(first.Payload)).Value);

        lease = new AuthorityLease("room-a", "authority-b", "controller-a", 5);
        network.SendAll(new AuthorityProbeMessage { Value = 13 });
        NetworkCampaignAuthoritySyncEnvelope nextEpoch = DeserializeEnvelope(network.LastBroadcast, serializer);
        Assert.Equal(5, nextEpoch.Epoch);
        Assert.Equal(1, nextEpoch.Sequence);

        lease = new AuthorityLease("room-b", "authority-b", "controller-a", 5);
        network.SendAll(new AuthorityProbeMessage { Value = 14 });
        NetworkCampaignAuthoritySyncEnvelope nextRoom = DeserializeEnvelope(network.LastBroadcast, serializer);
        Assert.Equal("room-b", nextRoom.RoomId);
        Assert.Equal(1, nextRoom.Sequence);
    }

    [Fact]
    public void AuthorityScopedMessages_QueueInTheSameOrderAsTheirSequenceNumbers()
    {
        var config = Mock.Of<INetworkConfig>();
        var serializer = new ProtoBufSerializer(new SerializableTypeMapper());
        var batcher = new ReliableMessageBatcher<NetPeer>(serializer);
        using var cancellation = new CancellationTokenSource();
        var authorityContext = new Mock<ICampaignAuthorityContext>();
        var lease = new AuthorityLease("room-a", "authority-a", "controller-a", 1);
        authorityContext.Setup(context => context.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease current) =>
            {
                current = lease;
                return true;
            });
        using var network = new TestNetwork(config, serializer, batcher, cancellation, authorityContext.Object);

        Parallel.For(0, 100, value => network.SendAll(new AuthorityProbeMessage { Value = value }));

        long[] queuedSequences = network.Broadcasts
            .Select(packet => DeserializeEnvelope(packet, serializer).Sequence)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), queuedSequences);
    }

    private static NetworkCampaignAuthoritySyncEnvelope DeserializeEnvelope(IPacket? packet, ICommonSerializer serializer)
    {
        var messagePacket = Assert.IsType<MessagePacket>(packet);
        return Assert.IsType<NetworkCampaignAuthoritySyncEnvelope>(serializer.Deserialize<IMessage>(messagePacket.Data));
    }

    [ProtoContract]
    public sealed class AuthorityProbeMessage : ICampaignAuthorityScopedMessage
    {
        [ProtoMember(1)]
        public int Value { get; set; }
    }

    private sealed class TestNetwork : CoopNetworkBase
    {
        public TestNetwork(
            INetworkConfig config,
            ICommonSerializer serializer,
            IReliableMessageBatcher<NetPeer> reliableMessageBatcher,
            CancellationTokenSource sessionCancellation,
            ICampaignAuthorityContext? authorityContext = null)
            : base(config, serializer, reliableMessageBatcher, sessionCancellation, authorityContext)
        {
        }

        public IPacket? LastBroadcast { get; private set; }
        public ConcurrentQueue<IPacket> Broadcasts { get; } = new ConcurrentQueue<IPacket>();

        public int AppliedDisconnectTimeout => netManager.DisconnectTimeout;

        public override int Priority => 0;

        public override void Start()
        {
        }

        public override void SendAll(IPacket packet)
        {
            LastBroadcast = packet;
            Broadcasts.Enqueue(packet);
        }

        public override void SendAllBut(NetPeer ignoredPeer, IPacket packet)
        {
        }

        public override void Update(TimeSpan frameTime)
        {
        }

        public override void OnPeerConnected(NetPeer peer)
        {
        }

        public override void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
        }

        public override void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }

        public override void OnNetworkReceive(
            NetPeer peer,
            NetPacketReader reader,
            byte channelNumber,
            DeliveryMethod deliveryMethod)
        {
        }

        public override void OnNetworkReceiveUnconnected(
            IPEndPoint remoteEndPoint,
            NetPacketReader reader,
            UnconnectedMessageType messageType)
        {
        }

        public override void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        public override void OnConnectionRequest(ConnectionRequest request)
        {
        }
    }
}
