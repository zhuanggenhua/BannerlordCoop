using Common.PacketHandlers;
using GameInterface.Services.MobileParties.Data;
using LiteNetLib;
using ProtoBuf;

namespace Coop.Core.Server.Services.MobileParties.Packets;

/// <summary>
/// Party behavior update request from client to server
/// </summary>
[ProtoContract(SkipConstructor = true)]
public struct RequestMobilePartyBehaviorPacket : IPacket
{
    public PacketType PacketType => PacketType.RequestUpdatePartyBehavior;
    public DeliveryMethod DeliveryMethod => DeliveryMethod.ReliableOrdered;
    [ProtoMember(1)]
    public NetworkPartyBehaviorUpdateData BehaviorUpdateData { get; }
    [ProtoMember(2)]
    public long RequestSequence { get; }
    [ProtoMember(3)] public string RoomId { get; }
    [ProtoMember(4)] public string AuthorityId { get; }
    [ProtoMember(5)] public string AuthorityControllerId { get; }
    [ProtoMember(6)] public long Epoch { get; }

    public RequestMobilePartyBehaviorPacket(
        NetworkPartyBehaviorUpdateData behaviorUpdateData,
        long requestSequence = 0,
        AuthorityLease lease = default)
    {
        BehaviorUpdateData = behaviorUpdateData;
        RequestSequence = requestSequence;
        RoomId = lease.RoomId ?? string.Empty;
        AuthorityId = lease.AuthorityId ?? string.Empty;
        AuthorityControllerId = lease.AuthorityControllerId ?? string.Empty;
        Epoch = lease.Epoch;
    }

    public bool MatchesLease(AuthorityLease lease) =>
        lease.IsValid && !lease.IsPaused && RoomId == lease.RoomId &&
        AuthorityId == lease.AuthorityId && AuthorityControllerId == lease.AuthorityControllerId &&
        Epoch == lease.Epoch;
}
