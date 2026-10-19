using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using Coop.Core.Client;
using Coop.Core.Client.Messages;
using Coop.Tests.Mocks;
using LiteNetLib;
using Moq;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace Coop.Tests.Client;

public sealed class CoopClientRoomHostPeerTests
{
    static CoopClientRoomHostPeerTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void OnlyConnectedCallbackBindsTheHostWithoutPlayerRegistry()
    {
        using var f = new Fixture();
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
        Assert.False(f.Client.IsCurrentRoomHostPeer(null));
        f.Client.OnPeerConnected(f.First);
        Assert.True(f.Client.IsCurrentRoomHostPeer(f.First));
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.Second));
    }

    [Fact]
    public void ReplacementRevokesOldPeerEvenWhenBothUseTheSameEndpoint()
    {
        using var f = new Fixture();
        f.Client.OnPeerConnected(f.First);
        f.Client.OnPeerConnected(f.Second);
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
        Assert.True(f.Client.IsCurrentRoomHostPeer(f.Second));
    }

    [Fact]
    public void LateOldDisconnectDoesNotRevokeOrTearDownReplacement()
    {
        using var f = new Fixture();
        f.Client.OnPeerConnected(f.First);
        f.Client.OnPeerConnected(f.Second);
        f.Client.OnPeerDisconnected(f.First, default);
        Assert.True(f.Client.IsCurrentRoomHostPeer(f.Second));
        f.Broker.Verify(value => value.Publish(It.IsAny<object>(), It.IsAny<NetworkDisconnected>()), Times.Never);
        f.Client.OnPeerDisconnected(f.Second, default);
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.Second));
        f.Broker.Verify(value => value.Publish(It.IsAny<object>(), It.IsAny<NetworkDisconnected>()), Times.Once);
    }

    [Fact]
    public void NativeDisconnectedStateFailsBeforeDisconnectCallback()
    {
        using var f = new Fixture();
        f.Client.OnPeerConnected(f.First);
        SetState(f.First, ConnectionState.Disconnected);
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
    }

    [Fact]
    public void CurrentDisconnectRevokesThenReconnectRequiresItsNewReference()
    {
        using var f = new Fixture();
        f.Client.OnPeerConnected(f.First);
        f.Client.OnPeerDisconnected(f.First, default);
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
        f.Client.OnPeerConnected(f.Second);
        Assert.True(f.Client.IsCurrentRoomHostPeer(f.Second));
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
    }

    [Fact]
    public void DisposalRevokesAndLateCallbacksCannotResurrectHost()
    {
        using var f = new Fixture();
        f.Client.OnPeerConnected(f.First);
        f.Client.Dispose();
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.First));
        f.Client.OnPeerConnected(f.Second);
        f.Client.OnPeerDisconnected(f.First, default);
        Assert.False(f.Client.IsCurrentRoomHostPeer(f.Second));
    }

    private static void SetState(NetPeer peer, ConnectionState state) =>
        typeof(NetPeer).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(peer, state);

    private sealed class Fixture : IDisposable
    {
        private readonly TestNetwork peers = new();
        private readonly CancellationTokenSource cancellation = new();
        public readonly Mock<IMessageBroker> Broker = new();
        public readonly CoopClient Client;
        public readonly NetPeer First, Second;

        public Fixture()
        {
            First = peers.CreatePeer();
            Second = peers.CreatePeer();
            SetState(First, ConnectionState.Connected);
            SetState(Second, ConnectionState.Connected);
            Client = new CoopClient(Mock.Of<INetworkConfig>(), Broker.Object,
                Mock.Of<IPacketManager>(), Mock.Of<IMessagePacketHandler>(), Mock.Of<ICommonSerializer>(),
                Mock.Of<IReliableMessageBatcher<NetPeer>>(), cancellation);
        }

        public void Dispose()
        {
            Client.Dispose();
            peers.Dispose();
            cancellation.Dispose();
        }
    }
}
