using LiteNetLib;
using System;

namespace Common.Network;

public interface IMissionPacketSourcePolicy
{
    bool TryCapture(NetPeer peer, string senderControllerId, out MissionPacketSourceReceipt receipt);
    bool IsCurrent(MissionPacketSourceReceipt receipt);
}

public abstract class MissionPacketSourceReceipt
{
    public NetPeer Peer { get; }
    public string ControllerId { get; }

    protected MissionPacketSourceReceipt(NetPeer peer, string controllerId)
    {
        if (peer == null) throw new ArgumentNullException(nameof(peer));
        if (string.IsNullOrWhiteSpace(controllerId)) throw new ArgumentException("A sender controller is required", nameof(controllerId));
        Peer = peer;
        ControllerId = controllerId;
    }
}
