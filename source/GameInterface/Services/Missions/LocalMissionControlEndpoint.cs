using System;

namespace GameInterface.Services.Missions;

public sealed class LocalMissionControlEndpoint
{
    private readonly ISessionIdentity session;
    public string RoomId { get; }
    public string RoomHostControllerId { get; }
    public string TargetControllerId { get; }

    public LocalMissionControlEndpoint(ISessionIdentity session, string targetControllerId)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(targetControllerId)) throw new ArgumentException("A target controller is required", nameof(targetControllerId));
        this.session = session;
        RoomId = session.RoomId;
        RoomHostControllerId = session.RoomHostControllerId;
        TargetControllerId = targetControllerId;
    }

    public bool IsCurrent(ISessionIdentity currentSession, string localControllerId) =>
        ReferenceEquals(session, currentSession) && currentSession.IsConfirmed &&
        !string.IsNullOrWhiteSpace(RoomId) && RoomId == currentSession.RoomId &&
        !string.IsNullOrWhiteSpace(RoomHostControllerId) &&
        RoomHostControllerId == currentSession.RoomHostControllerId &&
        RoomHostControllerId == localControllerId && TargetControllerId == localControllerId;
}
