using System;

/// <summary>Transport/session identities are distinct from the locally controlled campaign actor.</summary>
public interface ISessionIdentity
{
    AuthorityLease CurrentLease { get; }
    string RoomId { get; }
    string AuthorityId { get; }
    string AuthorityControllerId { get; }
    string RoomHostControllerId { get; }
    long Epoch { get; }
    bool IsConfirmed { get; }
    bool TryGetConfirmedLease(out AuthorityLease lease);
    bool TryApply(AuthorityLease lease);
    bool TryApply(AuthorityLease lease, string roomHostControllerId);
    bool TryRestore(AuthorityLease lease);
    bool TryRestore(AuthorityLease lease, string roomHostControllerId);
    bool TryConfirmCurrentLease();
    bool TrySetAuthorityControllerId(string controllerId);
    bool TrySetRoomHostControllerId(string controllerId);
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
    private string roomHostControllerId = string.Empty;
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
    public string RoomHostControllerId { get { lock (gate) return roomHostControllerId; } }
    public long Epoch => CurrentLease.Epoch;
    public bool IsConfirmed { get { lock (gate) return isConfirmed; } }

    public bool TryGetConfirmedLease(out AuthorityLease lease)
    {
        lock (gate)
        {
            if (!isConfirmed || !currentLease.IsValid)
            {
                lease = default;
                return false;
            }

            lease = currentLease;
            return true;
        }
    }

    public bool TryApply(AuthorityLease lease)
    {
        return TryApply(lease, null, applyRoomHost: false);
    }

    public bool TryApply(AuthorityLease lease, string roomHostControllerId)
    {
        return TryApply(lease, roomHostControllerId, applyRoomHost: true);
    }

    private bool TryApply(AuthorityLease lease, string roomHostControllerId, bool applyRoomHost)
    {
        if (!lease.IsValid) return false;

        lock (gate)
        {
            if (applyRoomHost && !CanApplyRoomHostController(roomHostControllerId)) return false;
            if (!CanApplyLease(lease)) return false;

            bool applyNewLease = !isConfirmed || lease.Epoch > currentLease.Epoch;
            if (applyNewLease) currentLease = lease;
            isConfirmed = true;
            if (applyRoomHost && !string.IsNullOrWhiteSpace(roomHostControllerId))
                this.roomHostControllerId = roomHostControllerId;
            return true;
        }
    }

    public bool TryRestore(AuthorityLease lease)
    {
        return TryApply(lease);
    }

    public bool TryRestore(AuthorityLease lease, string roomHostControllerId)
    {
        return TryApply(lease, roomHostControllerId);
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

    public bool TrySetRoomHostControllerId(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) return true;

        lock (gate)
        {
            if (string.Equals(roomHostControllerId, controllerId, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(roomHostControllerId)) return false;

            roomHostControllerId = controllerId;
            return true;
        }
    }

    private bool CanApplyLease(AuthorityLease lease)
    {
        if (!isConfirmed) return true;
        if (!string.Equals(currentLease.RoomId, lease.RoomId, StringComparison.Ordinal) ||
            lease.Epoch < currentLease.Epoch)
            return false;

        if (lease.Epoch == currentLease.Epoch)
            return currentLease == lease;

        return !string.Equals(currentLease.AuthorityId, lease.AuthorityId, StringComparison.Ordinal);
    }

    private bool CanApplyRoomHostController(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) return true;

        return string.IsNullOrEmpty(roomHostControllerId) ||
            string.Equals(roomHostControllerId, controllerId, StringComparison.Ordinal);
    }
}
