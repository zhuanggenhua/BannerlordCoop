using Common.Messaging;
using ProtoBuf;

namespace Common.Network.Session;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignMovementBaselineApplied : IMessage
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string PausedAuthorityId;
    [ProtoMember(3)] public readonly long PausedEpoch;
    [ProtoMember(4)] public readonly string ControllerId;
    [ProtoMember(5)] public readonly long Ticks;
    [ProtoMember(6)] public readonly string Sha256;

    public NetworkCampaignMovementBaselineApplied(AuthorityLease paused, string controllerId, CampaignMovementCheckpoint checkpoint)
    {
        RoomId = paused.RoomId;
        PausedAuthorityId = paused.AuthorityId;
        PausedEpoch = paused.Epoch;
        ControllerId = controllerId;
        Ticks = checkpoint.Ticks;
        Sha256 = checkpoint.Sha256;
    }
}
