using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Tests.Utils;
using Coop.Core.Client;
using Coop.Core.Client.Messages;
using Coop.Core.Server;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.Instances;
using Coop.Core.Server.Services.Shutdown;
using Coop.Core.Server.Services.Time;
using Coop.Tests.Mocks;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Messages;
using LiteNetLib;
using Moq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Xunit;

namespace Coop.Tests.Server;

[Collection(ModInformationRoleCollection.Name)]
public class CoopServerAuthorityAdmissionTests
{
    private const string CheckpointPopup = "The campaign authority checkpoint is unavailable. Joining is paused until the current writer completes a handoff.";

    [Fact]
    public void LeaseChangeAfterRequestAcceptanceStillRejectsPeerRegistration()
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room", "local", "host", 3), "host"));
            using var broker = new TestMessageBroker();
            using var cancellation = new CancellationTokenSource();
            var terminator = new Mock<IJoinPeerTerminator>();
            using var server = CreateServer(broker, identity, cancellation, terminator.Object);
            Assert.True(identity.TryApply(new AuthorityLease("room", "remote", "guest", 4), "host"));
            var peer = new TestNetwork().CreatePeer();
            server.OnPeerConnected(peer);
            Assert.Empty(broker.GetMessagesFromType<PlayerConnected>());
            terminator.Verify(value => value.Disconnect(peer, "CampaignCheckpointUnavailable"), Times.Once);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, "guest", false, true, false)]
    [InlineData(SessionMode.ListenAuthority, "host", false, true, true)]
    [InlineData(SessionMode.ListenAuthority, "", true, true, true)]
    [InlineData(SessionMode.ListenAuthority, "guest", false, false, true)]
    [InlineData(SessionMode.DedicatedAuthority, "guest", false, true, true)]
    public void PeerRegistrationRequiresAHostOwnedOrCommittedPausedCheckpoint(
        SessionMode mode, string writer, bool paused, bool confirmed, bool admitted)
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = mode;
        try
        {
            var identity = new SessionIdentity();
            if (confirmed) Assert.True(identity.TryApply(new AuthorityLease("room", "lease", writer, 3, paused), "host"));
            using var broker = new TestMessageBroker();
            using var cancellation = new CancellationTokenSource();
            var terminator = new Mock<IJoinPeerTerminator>();
            using var server = CreateServer(broker, identity, cancellation, terminator.Object);
            var peer = new TestNetwork().CreatePeer();
            server.OnPeerConnected(peer);
            Assert.Equal(admitted ? 1 : 0, broker.GetMessagesFromType<PlayerConnected>().Count());
            terminator.Verify(value => value.Disconnect(peer, "CampaignCheckpointUnavailable"), admitted ? Times.Never() : Times.Once());
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Theory]
    [InlineData("password", CheckpointPopup)]
    [InlineData("wrong", "The server password is incorrect.")]
    public void RemoteWriterAdmissionRefusalHasAnHonestPopupOverRealUdp(string suppliedPassword, string expected)
    {
        var previous = ModInformation.SessionMode;
        ModInformation.SessionMode = SessionMode.ListenAuthority;
        try
        {
            var identity = new SessionIdentity();
            Assert.True(identity.TryApply(new AuthorityLease("room", "remote", "guest", 3), "host"));
            using var broker = new TestMessageBroker();
            using var cancellation = new CancellationTokenSource();
            using var server = CreateServer(broker, identity, cancellation, Mock.Of<IJoinPeerTerminator>());
            using var client = new CoopClient(Config().Object, broker, Mock.Of<IPacketManager>(),
                Mock.Of<IMessagePacketHandler>(), Mock.Of<ICommonSerializer>(),
                Mock.Of<IReliableMessageBatcher<NetPeer>>(), cancellation);
            var listener = new EventBasedNetListener();
            listener.ConnectionRequestEvent += server.OnConnectionRequest;
            var serverTransport = new NetManager(listener);
            var clientTransport = new NetManager(client);
            try
            {
                Assert.True(serverTransport.StartInManualMode(0));
                Assert.True(clientTransport.StartInManualMode(0));
                clientTransport.Connect(IPAddress.Loopback.ToString(), serverTransport.LocalPort, suppliedPassword);
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(5) &&
                    !broker.GetMessagesFromType<SendPopupMessage>().Any() &&
                    !broker.GetMessagesFromType<NetworkConnected>().Any())
                {
                    serverTransport.ManualUpdate(15);
                    clientTransport.ManualUpdate(15);
                    serverTransport.PollEvents();
                    using (GameThread.ActivateCancellation(cancellation.Token)) clientTransport.PollEvents();
                    Thread.Sleep(1);
                }
                Assert.Equal(expected, Assert.Single(broker.GetMessagesFromType<SendPopupMessage>()).Text);
                Assert.Empty(broker.GetMessagesFromType<NetworkConnected>());
            }
            finally { clientTransport.Stop(); serverTransport.Stop(); }
        }
        finally { ModInformation.SessionMode = previous; }
    }

    private static CoopServer CreateServer(TestMessageBroker broker, ISessionIdentity identity,
        CancellationTokenSource cancellation, IJoinPeerTerminator terminator) => new CoopServer(
        Config().Object, broker, Mock.Of<IPacketManager>(), Mock.Of<IMessagePacketHandler>(),
        Mock.Of<IConnectionMessageQueue>(), new ServerAdmissionGate(), terminator,
        Mock.Of<IControllerIdProvider>(), Mock.Of<IMissionManager>(),
        new Lazy<IOverloadedPeerManager>(() => Mock.Of<IOverloadedPeerManager>()), Mock.Of<ISendCoalescer>(),
        new UdpBindDiagnostics(), Mock.Of<ICommonSerializer>(), Mock.Of<IReliableMessageBatcher<NetPeer>>(),
        cancellation, sessionIdentity: identity);

    private static Mock<INetworkConfig> Config()
    {
        var config = new Mock<INetworkConfig>();
        config.SetupGet(value => value.Token).Returns("password");
        config.SetupGet(value => value.DisconnectTimeout).Returns(TimeSpan.FromSeconds(5));
        config.SetupGet(value => value.UpdateTime).Returns(TimeSpan.FromMilliseconds(15));
        config.SetupGet(value => value.NetworkPollInterval).Returns(TimeSpan.FromMilliseconds(25));
        return config;
    }
}
