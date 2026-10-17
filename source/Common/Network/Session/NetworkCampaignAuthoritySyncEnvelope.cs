using Common.Messaging;
using ProtoBuf;

namespace Common.Network.Session;

/// <summary>Epoch-fenced AutoSync delta emitted by the active campaign authority.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkCampaignAuthoritySyncEnvelope : IMessage
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly string AuthorityId;
    [ProtoMember(3)] public readonly string AuthorityControllerId;
    [ProtoMember(4)] public readonly long Epoch;
    [ProtoMember(5)] public readonly long Sequence;
    [ProtoMember(6)] public readonly byte[] Payload;

    public NetworkCampaignAuthoritySyncEnvelope(
        string roomId,
        string authorityId,
        string authorityControllerId,
        long epoch,
        long sequence,
        byte[] payload)
    {
        RoomId = roomId ?? string.Empty;
        AuthorityId = authorityId ?? string.Empty;
        AuthorityControllerId = authorityControllerId ?? string.Empty;
        Epoch = epoch;
        Sequence = sequence;
        Payload = payload ?? System.Array.Empty<byte>();
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(RoomId) &&
        !string.IsNullOrWhiteSpace(AuthorityId) &&
        Epoch > 0 && Sequence > 0 && Payload != null && Payload.Length > 0;
}
