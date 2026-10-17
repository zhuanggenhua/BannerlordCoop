using Common.Messaging;
using ProtoBuf;

namespace Common.Network.Session;

/// <summary>Initial campaign authority identity sent to a joining replica.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkAuthorityLeaseSnapshot : IServerToClientCommand
{
    [ProtoMember(1)]
    public readonly string RoomId;
    [ProtoMember(2)]
    public readonly string AuthorityId;
    [ProtoMember(3)]
    public readonly string AuthorityControllerId;
    [ProtoMember(4)]
    public readonly long Epoch;
    [ProtoMember(5)]
    public readonly string RoomHostControllerId;
    [ProtoMember(6)]
    public readonly bool IsPaused;

    public NetworkAuthorityLeaseSnapshot(AuthorityLease lease, string roomHostControllerId = null)
    {
        RoomId = lease.RoomId;
        AuthorityId = lease.AuthorityId;
        AuthorityControllerId = lease.AuthorityControllerId;
        Epoch = lease.Epoch;
        RoomHostControllerId = roomHostControllerId ?? string.Empty;
        IsPaused = lease.IsPaused;
    }

    public AuthorityLease ToLease() =>
        new AuthorityLease(RoomId, AuthorityId, AuthorityControllerId, Epoch, IsPaused);
}
