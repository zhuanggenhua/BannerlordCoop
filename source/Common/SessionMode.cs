using System;

namespace Common;

public enum SessionMode
{
    RemoteReplica,
    DedicatedAuthority,
    ListenAuthority,
}

/// <summary>Transport/session identities are distinct from the locally controlled campaign actor.</summary>
public interface ISessionIdentity
{
    string RoomId { get; }
    string AuthorityId { get; }
}

public sealed class SessionIdentity : ISessionIdentity
{
    public string RoomId { get; } = Guid.NewGuid().ToString("N");
    public string AuthorityId { get; } = Guid.NewGuid().ToString("N");
}
