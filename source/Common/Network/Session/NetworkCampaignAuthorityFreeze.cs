using Common.Messaging;
using ProtoBuf;

namespace Common.Network.Session;

/// <summary>Asks the current remote authority to stop producing campaign writes before lease transfer.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityFreezeRequest : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly string CandidateControllerId;
    [ProtoMember(6)] public readonly long HandoffSequence;

    public NetworkCampaignAuthorityFreezeRequest(
        AuthorityLease lease,
        string candidateControllerId,
        long handoffSequence)
    {
        RoomId = lease.RoomId;
        AuthorityId = lease.AuthorityId;
        AuthorityControllerId = lease.AuthorityControllerId;
        Epoch = lease.Epoch;
        CandidateControllerId = candidateControllerId ?? string.Empty;
        HandoffSequence = handoffSequence;
    }

    public NetworkCampaignAuthorityFreezeAcknowledged ToAcknowledged(RegistryAllocationCheckpoint allocationCheckpoint = null) =>
        new NetworkCampaignAuthorityFreezeAcknowledged(
            RoomId, AuthorityId, AuthorityControllerId, Epoch, CandidateControllerId, HandoffSequence, allocationCheckpoint);

    public NetworkCampaignAuthorityFreezeCanceled ToCanceled() =>
        new NetworkCampaignAuthorityFreezeCanceled(
            RoomId, AuthorityId, AuthorityControllerId, Epoch, CandidateControllerId, HandoffSequence);
}

/// <summary>The old authority has fenced its local writes and flushed earlier campaign messages.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityFreezeAcknowledged : IMessage
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly string CandidateControllerId;
    [ProtoMember(6)] public readonly long HandoffSequence;
    [ProtoMember(7)] public readonly RegistryAllocationCheckpoint AllocationCheckpoint;

    public NetworkCampaignAuthorityFreezeAcknowledged(
        string roomId,
        string authorityId,
        string authorityControllerId,
        long epoch,
        string candidateControllerId,
        long handoffSequence,
        RegistryAllocationCheckpoint allocationCheckpoint = null)
    {
        RoomId = roomId ?? string.Empty;
        AuthorityId = authorityId ?? string.Empty;
        AuthorityControllerId = authorityControllerId ?? string.Empty;
        Epoch = epoch;
        CandidateControllerId = candidateControllerId ?? string.Empty;
        HandoffSequence = handoffSequence;
        AllocationCheckpoint = allocationCheckpoint;
    }
}

/// <summary>Releases a still-current authority when the preferred candidate changed before acknowledgement.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthorityFreezeCanceled : IServerToClientCommand
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly string CandidateControllerId;
    [ProtoMember(6)] public readonly long HandoffSequence;

    public NetworkCampaignAuthorityFreezeCanceled(
        string roomId,
        string authorityId,
        string authorityControllerId,
        long epoch,
        string candidateControllerId,
        long handoffSequence)
    {
        RoomId = roomId ?? string.Empty;
        AuthorityId = authorityId ?? string.Empty;
        AuthorityControllerId = authorityControllerId ?? string.Empty;
        Epoch = epoch;
        CandidateControllerId = candidateControllerId ?? string.Empty;
        HandoffSequence = handoffSequence;
    }
}
