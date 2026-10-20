using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Messages;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Tests.Utils;
using Coop.Core.Client;
using Coop.Core.Server;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Shutdown;
using Coop.Core.Server.Services.Time;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace Coop.Tests.Server;

public class CoopServerDisconnectDiagnosticsTests
{
    [Theory]
    [InlineData(DisconnectReason.Timeout, SocketError.TimedOut)]
    [InlineData(DisconnectReason.RemoteConnectionClose, SocketError.Success)]
    public void DisconnectLogsOriginalReasonBeforePublishingTheSamePeerExactlyOnce(DisconnectReason reason, SocketError error)
    {
        var logs = new ConcurrentQueue<string>();
        Action<string> capture = logs.Enqueue;
        OutputSinkManager.AddLogCallback(capture);
        try
        {
            using var broker = new TestMessageBroker();
            using var cancellation = new CancellationTokenSource();
            var peer = new TestNetwork().CreatePeer();
            var player = new Player("diagnostic-guest", "hero", "party", "clan", "character");
            var players = new Mock<IPlayerManager>();
            players.Setup(value => value.TryGetPlayer(peer, out player)).Returns(true);
            var identity = new SessionIdentity();
            var lease = new AuthorityLease("diagnostic-room", "diagnostic-writer", player.ControllerId, 5);
            Assert.True(identity.TryApply(lease, "host"));
            using var server = Create(broker, cancellation, identity, players.Object);
            bool publishedAfterLog = false;
            broker.Subscribe<PlayerDisconnected>(_ => publishedAfterLog = logs.Any(line => line.Contains("diagnostic-room")));
            var info = new DisconnectInfo { Reason = reason, SocketErrorCode = error };
            server.OnPeerDisconnected(peer, info);
            var message = Assert.Single(broker.GetMessagesFromType<PlayerDisconnected>());
            Assert.Same(peer, message.PlayerId);
            Assert.Equal(reason, message.DisconnectInfo.Reason);
            Assert.Equal(error, message.DisconnectInfo.SocketErrorCode);
            Assert.True(publishedAfterLog);
            string line = Assert.Single(logs, text => text.StartsWith("Room peer disconnected before publish:") && text.Contains("diagnostic-room"));
            Assert.Contains(reason.ToString(), line);
            Assert.Contains(error.ToString(), line);
            Assert.Contains("diagnostic-guest", line);
            Assert.Contains($"peerRef {RuntimeHelpers.GetHashCode(peer)}", line);
            Assert.Contains("127.0.0.1:5555", line);
            Assert.Equal(lease, identity.CurrentLease);
        }
        finally { OutputSinkManager.RemoveLogCallback(capture); }
    }

    [Fact]
    public void OriginalPublishExceptionStillEscapesUnchanged()
    {
        var broker = new Mock<IMessageBroker>();
        var failure = new InvalidOperationException("original-disconnect-publish-failure");
        broker.Setup(value => value.Publish(It.IsAny<object>(), It.IsAny<PlayerDisconnected>())).Throws(failure);
        using var cancellation = new CancellationTokenSource();
        using var server = Create(broker.Object, cancellation);
        var peer = new TestNetwork().CreatePeer();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => server.OnPeerDisconnected(peer, default)));
        broker.Verify(value => value.Publish(server, It.Is<PlayerDisconnected>(message => ReferenceEquals(message.PlayerId, peer))), Times.Once);
    }

    [Fact]
    public void ClientDiagnosticReportsAnObservedEmptyTransportWithoutStartingIt()
    {
        using var broker = new TestMessageBroker();
        using var cancellation = new CancellationTokenSource();
        using var client = new CoopClient(Config(), broker, Mock.Of<IPacketManager>(), Mock.Of<IMessagePacketHandler>(),
            Mock.Of<ICommonSerializer>(), Mock.Of<IReliableMessageBatcher<NetPeer>>(), cancellation);
        Assert.Contains("connected=0;", client.DescribeRoomPeers());
        Assert.Contains("peers=[]", client.DescribeRoomPeers());
        client.Dispose();
        Assert.Equal("Unavailable(disposed CoopClient transport)", client.DescribeRoomPeers());
    }

    private static CoopServer Create(IMessageBroker broker, CancellationTokenSource cancellation,
        ISessionIdentity identity = null, IPlayerManager players = null) => new(
        Config(), broker, Mock.Of<IPacketManager>(), Mock.Of<IMessagePacketHandler>(),
        Mock.Of<IConnectionMessageQueue>(), Mock.Of<IServerAdmissionGate>(), Mock.Of<IJoinPeerTerminator>(),
        Mock.Of<IControllerIdProvider>(), Mock.Of<IMissionManager>(),
        new Lazy<IOverloadedPeerManager>(() => Mock.Of<IOverloadedPeerManager>()), Mock.Of<ISendCoalescer>(),
        new UdpBindDiagnostics(), Mock.Of<ICommonSerializer>(), Mock.Of<IReliableMessageBatcher<NetPeer>>(), cancellation,
        sessionIdentity: identity, playerManager: players);

    private static INetworkConfig Config() => Mock.Of<INetworkConfig>(value =>
        value.DisconnectTimeout == TimeSpan.FromSeconds(5) && value.UpdateTime == TimeSpan.FromMilliseconds(15) &&
        value.NetworkPollInterval == TimeSpan.FromMilliseconds(25));
}
