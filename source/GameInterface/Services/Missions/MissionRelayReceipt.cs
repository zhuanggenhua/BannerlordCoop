using Common.Network;
using LiteNetLib;
using System;

namespace GameInterface.Services.Missions;

public interface IMissionRelaySourceVerifier
{
    bool TryCaptureLocalDelivery(NetPeer sourcePeer, string instanceId, string localControllerId, out MissionRelayReceipt receipt);
    bool IsCurrent(MissionRelayReceipt receipt);
}

public interface ILocalMissionRelayReceiver
{
    bool TryReceive(MissionRelayReceipt receipt, byte[] payload);
}

public abstract class MissionRelayReceipt : MissionPacketSourceReceipt
{
    public string InstanceId { get; }
    public string TargetControllerId { get; }
    public Guid SourceCredential { get; }
    public Guid TargetCredential { get; }
    public long SourceGeneration { get; }
    public long TargetGeneration { get; }

    protected MissionRelayReceipt(NetPeer sourcePeer, string sourceControllerId, string instanceId,
        string targetControllerId, Guid sourceCredential, Guid targetCredential,
        long sourceGeneration, long targetGeneration) : base(sourcePeer, sourceControllerId)
    {
        InstanceId = instanceId;
        TargetControllerId = targetControllerId;
        SourceCredential = sourceCredential;
        TargetCredential = targetCredential;
        SourceGeneration = sourceGeneration;
        TargetGeneration = targetGeneration;
    }
}
