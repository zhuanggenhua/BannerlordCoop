using LiteNetLib;
using System;

namespace GameInterface.Services.Missions;

public abstract class MissionPlayerEndpoint
{
    public string ControllerId { get; }
    public abstract NetPeer Peer { get; }

    private protected MissionPlayerEndpoint(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) throw new ArgumentException("A player controller is required", nameof(controllerId));
        ControllerId = controllerId;
    }
}

public sealed class LocalPlayerEndpoint : MissionPlayerEndpoint
{
    public override NetPeer Peer => null;

    public LocalPlayerEndpoint(string controllerId) : base(controllerId) { }
}

public sealed class RemotePeerEndpoint : MissionPlayerEndpoint
{
    public override NetPeer Peer { get; }

    public RemotePeerEndpoint(string controllerId, NetPeer peer) : base(controllerId)
    {
        if (peer == null) throw new ArgumentNullException(nameof(peer));
        Peer = peer;
    }
}
