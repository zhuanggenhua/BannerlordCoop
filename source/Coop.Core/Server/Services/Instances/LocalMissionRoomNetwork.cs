using Common.Messaging;
using Common.Network;
using Common.PacketHandlers;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using LiteNetLib;
using System;
using System.Net;

namespace Coop.Core.Server.Services.Instances;

public sealed class LocalMissionRoomNetwork : IRelayNetwork
{
    private readonly ICoopServer server;
    private readonly IMessageBroker broker;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly IMissionManager missionManager;

    public INetworkConfig Config => server.Config;
    public IPEndPoint ServerEndpoint => new IPEndPoint(IPAddress.Loopback, Config.Port);

    public LocalMissionRoomNetwork(ICoopServer server, IMessageBroker broker,
        IControllerIdProvider controllerIdProvider, IMissionManager missionManager)
    {
        this.server = server;
        this.broker = broker;
        this.controllerIdProvider = controllerIdProvider;
        this.missionManager = missionManager;
    }

    public void SendAll(IMessage message) => broker.PublishRuntime(
        new LocalPlayerEndpoint(controllerIdProvider.ControllerId), message);

    public void SendAll(IPacket packet)
    {
        if (packet is not RelayPacket relay) throw new NotSupportedException("Only Mission relay packets target room control");
        var source = new LocalPlayerEndpoint(controllerIdProvider.ControllerId);
        if (!missionManager.TryGetRelayTarget(source, relay.InstanceId, relay.ControllerId, out var target))
            throw new InvalidOperationException("Mission relay target is not a current remote instance member");
        target.Send(relay.Payload, relay.DeliveryMethod);
    }

    public void Send(NetPeer peer, IPacket packet) => server.Send(peer, packet);
    public void SendImmediate(NetPeer peer, IPacket packet) => server.SendImmediate(peer, packet);
    public void SendAllBut(NetPeer peer, IPacket packet) => server.SendAllBut(peer, packet);
    public void Send(NetPeer peer, IMessage message) => server.Send(peer, message);
    public void SendImmediate(NetPeer peer, IMessage message) => server.SendImmediate(peer, message);
    public void SendAllBut(NetPeer peer, IMessage message) => server.SendAllBut(peer, message);
    public void FlushPendingMessages() => server.FlushPendingMessages();
    public void Start() { }
    public void Dispose() { }
}
