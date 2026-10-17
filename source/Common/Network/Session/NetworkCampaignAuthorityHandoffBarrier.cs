using Common.Messaging;
using ProtoBuf;

namespace Common.Network.Session;

/// <summary>
/// Marks the reliable-ordered stream position after the current authority has frozen its campaign.
/// A candidate acknowledges only after earlier queued campaign applies have drained on its game thread.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityHandoffBarrier : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string PausedAuthorityId;
    [ProtoMember(3)] public readonly long PausedEpoch;
    [ProtoMember(4)] public readonly string CandidateControllerId;
    [ProtoMember(5)] public readonly long BarrierSequence;

    public NetworkCampaignAuthorityHandoffBarrier(
        string roomId,
        string pausedAuthorityId,
        long pausedEpoch,
        string candidateControllerId,
        long barrierSequence)
    {
        RoomId = roomId ?? string.Empty;
        PausedAuthorityId = pausedAuthorityId ?? string.Empty;
        PausedEpoch = pausedEpoch;
        CandidateControllerId = candidateControllerId ?? string.Empty;
        BarrierSequence = barrierSequence;
    }

    public NetworkCampaignAuthorityHandoffBarrierApplied ToApplied() => new NetworkCampaignAuthorityHandoffBarrierApplied(
        RoomId, PausedAuthorityId, PausedEpoch, CandidateControllerId, BarrierSequence);
}

/// <summary>Candidate acknowledgement that the reliable-ordered stream has applied through a handoff barrier.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityHandoffBarrierApplied : IMessage
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string PausedAuthorityId;
    [ProtoMember(3)] public readonly long PausedEpoch;
    [ProtoMember(4)] public readonly string CandidateControllerId;
    [ProtoMember(5)] public readonly long BarrierSequence;

    public NetworkCampaignAuthorityHandoffBarrierApplied(
        string roomId,
        string pausedAuthorityId,
        long pausedEpoch,
        string candidateControllerId,
        long barrierSequence)
    {
        RoomId = roomId ?? string.Empty;
        PausedAuthorityId = pausedAuthorityId ?? string.Empty;
        PausedEpoch = pausedEpoch;
        CandidateControllerId = candidateControllerId ?? string.Empty;
        BarrierSequence = barrierSequence;
    }
}
