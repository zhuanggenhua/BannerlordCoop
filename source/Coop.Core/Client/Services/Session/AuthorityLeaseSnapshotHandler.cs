using Common;
using Common.Network;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.Time.Interfaces;
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
    private readonly ICampaignMovementCheckpointService movement;
    private readonly IMapTimeTrackerInterface clock;
    private readonly INetwork network;
    private readonly IControllerIdProvider controller;

    public AuthorityLeaseSnapshotHandler(IMessageBroker messageBroker, ISessionIdentity sessionIdentity,
        ICampaignMovementCheckpointService movement = null, IMapTimeTrackerInterface clock = null,
        INetwork network = null, IControllerIdProvider controller = null)
    {
        this.messageBroker = messageBroker;
        this.sessionIdentity = sessionIdentity;
        this.movement = movement;
        this.clock = clock;
        this.network = network;
        this.controller = controller;
        messageBroker.Subscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    private void Handle_AuthorityLeaseSnapshot(MessagePayload<NetworkAuthorityLeaseSnapshot> payload)
    {
        var snapshot = payload.What;
        GameThread.EnqueueSafe(() => ApplySnapshotOnGameThread(snapshot), context: nameof(AuthorityLeaseSnapshotHandler));
    }

    private void ApplySnapshotOnGameThread(NetworkAuthorityLeaseSnapshot snapshot)
    {
        var lease = snapshot.ToLease();
        if (lease.IsPaused && movement != null)
        {
            // Reject stale identity before preparing or installing any received campaign state.
            if (sessionIdentity.TryGetConfirmedLease(out var current) &&
                (current.RoomId != lease.RoomId || lease.Epoch < current.Epoch ||
                 (lease.Epoch == current.Epoch && lease != current) ||
                 (lease.Epoch > current.Epoch && (current.IsPaused || lease.AuthorityId == current.AuthorityId ||
                     snapshot.MovementCheckpoint?.SourceLease != current)))) return;
            if (sessionIdentity.RoomHostControllerId != null && sessionIdentity.RoomHostControllerId.Length > 0 &&
                sessionIdentity.RoomHostControllerId != snapshot.RoomHostControllerId) return;
            var checkpoint = snapshot.MovementCheckpoint;
            if (checkpoint == null || !checkpoint.Precedes(lease) ||
                !movement.TryPrepare(checkpoint, checkpoint.SourceLease, checkpoint.Ticks, out var install) ||
                clock == null || !clock.TryApplyAuthorityHandoffBaseline(checkpoint.Ticks) || !install())
            {
                Logger.Warning("Rejected paused lease epoch {Epoch}; final movement baseline is missing or incomplete", lease.Epoch);
                return;
            }
        }
        if (!sessionIdentity.TryApply(lease, snapshot.RoomHostControllerId))
        {
            Logger.Warning("Rejected campaign authority lease snapshot for room {RoomId} at epoch {Epoch} " +
                "with room host {RoomHostControllerId}; current room is {CurrentRoomId} at epoch {CurrentEpoch} " +
                "with room host {CurrentRoomHostControllerId}",
                lease.RoomId, lease.Epoch, snapshot.RoomHostControllerId,
                sessionIdentity.RoomId, sessionIdentity.Epoch, sessionIdentity.RoomHostControllerId);
            return;
        }

        if (lease.IsPaused && movement != null && network != null && controller != null)
        {
            network.SendAll(new NetworkCampaignMovementBaselineApplied(lease, controller.ControllerId, snapshot.MovementCheckpoint));
            network.FlushPendingMessages();
        }

        Logger.Information("Applied campaign authority lease for room {RoomId}, authority {AuthorityId}, " +
            "controller {ControllerId}, epoch {Epoch}",
            lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId, lease.Epoch);
    }
}
