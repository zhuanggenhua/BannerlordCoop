using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network.Session;
using Serilog;

namespace Coop.Core.Client.Services.Session;

/// <summary>Applies the room's initial lease and rejects stale or conflicting snapshots.</summary>
public sealed class AuthorityLeaseSnapshotHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<AuthorityLeaseSnapshotHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ISessionIdentity sessionIdentity;

    public AuthorityLeaseSnapshotHandler(IMessageBroker messageBroker, ISessionIdentity sessionIdentity)
    {
        this.messageBroker = messageBroker;
        this.sessionIdentity = sessionIdentity;
        messageBroker.Subscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    private void Handle_AuthorityLeaseSnapshot(MessagePayload<NetworkAuthorityLeaseSnapshot> payload)
    {
        var lease = payload.What.ToLease();
        if (!sessionIdentity.TryApply(lease))
        {
            Logger.Warning("Rejected campaign authority lease snapshot for room {RoomId} at epoch {Epoch}; " +
                "current room is {CurrentRoomId} at epoch {CurrentEpoch}",
                lease.RoomId, lease.Epoch, sessionIdentity.RoomId, sessionIdentity.Epoch);
            return;
        }

        Logger.Information("Applied campaign authority lease for room {RoomId}, authority {AuthorityId}, " +
            "controller {ControllerId}, epoch {Epoch}",
            lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId, lease.Epoch);
    }
}
