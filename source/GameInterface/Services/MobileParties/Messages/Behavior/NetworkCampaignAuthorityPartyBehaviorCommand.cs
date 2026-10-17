using Common.Messaging;
using GameInterface.Services.MobileParties.Data;
using ProtoBuf;

namespace GameInterface.Services.MobileParties.Messages.Behavior;

/// <summary>
/// A player-authored party behavior request relayed by the room host to the current campaign
/// authority. Lease fields fence commands delayed across handoff; sequence fences replay.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityPartyBehaviorCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly NetworkPartyBehaviorUpdateData BehaviorUpdateData;

    public NetworkCampaignAuthorityPartyBehaviorCommand(
        AuthorityLease lease,
        long sequence,
        string originControllerId,
        NetworkPartyBehaviorUpdateData behaviorUpdateData)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = originControllerId ?? string.Empty;
        BehaviorUpdateData = behaviorUpdateData;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId);
}
