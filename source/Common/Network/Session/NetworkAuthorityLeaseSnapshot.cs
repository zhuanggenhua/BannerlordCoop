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

    public NetworkAuthorityLeaseSnapshot(AuthorityLease lease)
    {
        RoomId = lease.RoomId;
        AuthorityId = lease.AuthorityId;
        AuthorityControllerId = lease.AuthorityControllerId;
        Epoch = lease.Epoch;
    }

    public AuthorityLease ToLease() => new AuthorityLease(RoomId, AuthorityId, AuthorityControllerId, Epoch);
}
