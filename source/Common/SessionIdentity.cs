using System;

/// <summary>Transport/session identities are distinct from the locally controlled campaign actor.</summary>
public interface ISessionIdentity
{
    AuthorityLease CurrentLease { get; }
    string RoomId { get; }
    string AuthorityId { get; }
    string AuthorityControllerId { get; }
    long Epoch { get; }
    bool IsConfirmed { get; }
    bool TryApply(AuthorityLease lease);
    bool TryRestore(AuthorityLease lease);
    bool TryConfirmCurrentLease();
    bool TrySetAuthorityControllerId(string controllerId);
}

public readonly struct AuthorityLease : IEquatable<AuthorityLease>
{
    public string RoomId { get; }
    public string AuthorityId { get; }
    public string AuthorityControllerId { get; }
    public long Epoch { get; }

    public AuthorityLease(string roomId, string authorityId, string authorityControllerId, long epoch)
    {
        RoomId = roomId ?? string.Empty;
        AuthorityId = authorityId ?? string.Empty;
        AuthorityControllerId = authorityControllerId ?? string.Empty;
        Epoch = epoch;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(RoomId) &&
        !string.IsNullOrWhiteSpace(AuthorityId) && Epoch > 0;

    public bool Equals(AuthorityLease other) =>
        string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
        string.Equals(AuthorityId, other.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(AuthorityControllerId, other.AuthorityControllerId, StringComparison.Ordinal) &&
        Epoch == other.Epoch;

    public override bool Equals(object obj) => obj is AuthorityLease other && Equals(other);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = RoomId?.GetHashCode() ?? 0;
            hash = (hash * 397) ^ (AuthorityId?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ (AuthorityControllerId?.GetHashCode() ?? 0);
            return (hash * 397) ^ Epoch.GetHashCode();
        }
    }
    public static bool operator ==(AuthorityLease left, AuthorityLease right) => left.Equals(right);
    public static bool operator !=(AuthorityLease left, AuthorityLease right) => !left.Equals(right);
}

public sealed class SessionIdentity : ISessionIdentity
{
    private readonly object gate = new object();
    private AuthorityLease currentLease;
    private bool isConfirmed;

    public SessionIdentity()
    {
        currentLease = new AuthorityLease(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            string.Empty,
            1);
    }

    public AuthorityLease CurrentLease
    {
        get { lock (gate) return currentLease; }
    }

    public string RoomId => CurrentLease.RoomId;
    public string AuthorityId => CurrentLease.AuthorityId;
    public string AuthorityControllerId => CurrentLease.AuthorityControllerId;
    public long Epoch => CurrentLease.Epoch;
    public bool IsConfirmed { get { lock (gate) return isConfirmed; } }

    public bool TryApply(AuthorityLease lease)
    {
        if (!lease.IsValid) return false;

        lock (gate)
        {
            if (!isConfirmed)
            {
                currentLease = lease;
                isConfirmed = true;
                return true;
            }

            if (!string.Equals(currentLease.RoomId, lease.RoomId, StringComparison.Ordinal) ||
                lease.Epoch < currentLease.Epoch)
                return false;

            if (lease.Epoch == currentLease.Epoch)
                return currentLease == lease;

            if (string.Equals(currentLease.AuthorityId, lease.AuthorityId, StringComparison.Ordinal))
                return false;

            currentLease = lease;
            return true;
        }
    }

    public bool TryRestore(AuthorityLease lease)
    {
        return TryApply(lease);
    }

    public bool TryConfirmCurrentLease()
    {
        lock (gate)
        {
            if (!currentLease.IsValid) return false;
            isConfirmed = true;
            return true;
        }
    }

    public bool TrySetAuthorityControllerId(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) return false;

        lock (gate)
        {
            if (string.Equals(currentLease.AuthorityControllerId, controllerId, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(currentLease.AuthorityControllerId)) return false;

            currentLease = new AuthorityLease(
                currentLease.RoomId,
                currentLease.AuthorityId,
                controllerId,
                currentLease.Epoch);
            return true;
        }
    }
}
