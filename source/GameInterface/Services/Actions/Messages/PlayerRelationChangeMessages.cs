using Common;
using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Actions.Messages;

/// <summary>Local request raised when a non-authority player changes a hero relationship.</summary>
public readonly struct PlayerRelationChangeRequested : IEvent
{
    public readonly Hero SourceHero;
    public readonly Hero TargetHero;
    public readonly int Relation;
    public readonly bool ShowNotification;
    public readonly long RequestSequence;
    public readonly AuthorityLease Lease;

    public PlayerRelationChangeRequested(
        Hero sourceHero,
        Hero targetHero,
        int relation,
        bool showNotification,
        long requestSequence,
        AuthorityLease lease)
    {
        SourceHero = sourceHero;
        TargetHero = targetHero;
        Relation = relation;
        ShowNotification = showNotification;
        RequestSequence = requestSequence;
        Lease = lease;
    }
}

/// <summary>A client request sent to the room host for validation and authority routing.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPlayerRelationChangeRequest : ICommand
{
    [ProtoMember(1)] public readonly string SourceHeroId;
    [ProtoMember(2)] public readonly string TargetHeroId;
    [ProtoMember(3)] public readonly int Relation;
    [ProtoMember(4)] public readonly bool ShowNotification;
    [ProtoMember(5)] public readonly long RequestSequence;
    [ProtoMember(6)] public readonly string RoomId;
    [ProtoMember(7)] public readonly string AuthorityId;
    [ProtoMember(8)] public readonly string AuthorityControllerId;
    [ProtoMember(9)] public readonly long Epoch;

    public NetworkPlayerRelationChangeRequest(
        string sourceHeroId,
        string targetHeroId,
        int relation,
        bool showNotification,
        long requestSequence,
        AuthorityLease lease)
    {
        SourceHeroId = sourceHeroId ?? string.Empty;
        TargetHeroId = targetHeroId ?? string.Empty;
        Relation = relation;
        ShowNotification = showNotification;
        RequestSequence = requestSequence;
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
    }

    public bool MatchesLease(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && RequestSequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch;
}

/// <summary>A validated, sequenced player relation request routed from the relay to the authority.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCampaignAuthorityPlayerRelationCommand : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly string OriginControllerId;
    [ProtoMember(7)] public readonly NetworkPlayerRelationChangeRequest Request;

    public NetworkCampaignAuthorityPlayerRelationCommand(
        AuthorityLease lease,
        long sequence,
        string originControllerId,
        NetworkPlayerRelationChangeRequest request)
    {
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
        Sequence = sequence;
        OriginControllerId = originControllerId ?? string.Empty;
        Request = request;
    }

    public bool Matches(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && Sequence > 0 &&
        RoomId == lease.RoomId && AuthorityId == lease.AuthorityId &&
        AuthorityControllerId == lease.AuthorityControllerId && Epoch == lease.Epoch &&
        !string.IsNullOrWhiteSpace(OriginControllerId) && Request.MatchesLease(lease) &&
        !string.IsNullOrWhiteSpace(Request.SourceHeroId) &&
        !string.IsNullOrWhiteSpace(Request.TargetHeroId);
}
