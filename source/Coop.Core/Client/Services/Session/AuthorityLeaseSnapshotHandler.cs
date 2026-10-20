using Common;
using Common.Session;
using Common.Network;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority.Movement;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Time.Interfaces;
using Common.Logging;
using Common.Messaging;
using Common.Network.Session;
using Serilog;
using LiteNetLib;
using System.Runtime.CompilerServices;
using System.Threading;

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
    private readonly ICampaignAsyncTaskDrain asyncTasks;

    public AuthorityLeaseSnapshotHandler(IMessageBroker messageBroker, ISessionIdentity sessionIdentity,
        ICampaignMovementCheckpointService movement = null, IMapTimeTrackerInterface clock = null,
        INetwork network = null, IControllerIdProvider controller = null, ICampaignAsyncTaskDrain asyncTasks = null)
    {
        this.messageBroker = messageBroker;
        this.sessionIdentity = sessionIdentity;
        CampaignApplyCompletion.Attach(messageBroker, sessionIdentity);
        this.movement = movement;
        this.clock = clock;
        this.network = network;
        this.controller = controller;
        this.asyncTasks = asyncTasks;
        messageBroker.Subscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    private void Handle_AuthorityLeaseSnapshot(MessagePayload<NetworkAuthorityLeaseSnapshot> payload)
    {
        var snapshot = payload.What;
        var peer = payload.Who as NetPeer;
        LogMovementAckStage("snapshot-ingress", snapshot, peer);
        GameThread.EnqueueSafe(() => ApplySnapshotOnGameThread(snapshot, peer), context: nameof(AuthorityLeaseSnapshotHandler));
        LogMovementAckStage("snapshot-queued", snapshot, peer);
    }

    private void ApplySnapshotOnGameThread(NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        var lease = snapshot.ToLease();
        LogMovementAckStage("snapshot-game-thread", snapshot, peer);
        if (lease.IsPaused && movement != null)
        {
            // Reject stale identity before preparing or installing any received campaign state.
            if (sessionIdentity.TryGetConfirmedLease(out var current) &&
                (current.RoomId != lease.RoomId || lease.Epoch < current.Epoch ||
                 (lease.Epoch == current.Epoch && lease != current) ||
                 (lease.Epoch > current.Epoch && (current.IsPaused || lease.AuthorityId == current.AuthorityId ||
                     snapshot.MovementCheckpoint?.SourceLease != current))))
            {
                LogMovementAckStage("snapshot-rejected-stale-or-conflicting-lease", snapshot, peer);
                return;
            }
            if (sessionIdentity.RoomHostControllerId != null && sessionIdentity.RoomHostControllerId.Length > 0 &&
                sessionIdentity.RoomHostControllerId != snapshot.RoomHostControllerId)
            {
                LogMovementAckStage("snapshot-rejected-room-host", snapshot, peer);
                return;
            }
            if (!CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out var applyReason))
            {
                Logger.Warning("Refused paused lease movement receipt at epoch {Epoch}: {ApplyReason}", lease.Epoch, applyReason);
                return;
            }
            if (RejectMovementAck(asyncTasks == null, "async-drain-unavailable", snapshot, peer) ||
                RejectMovementAck(!asyncTasks.TryDrain(), "async-drain-incomplete", snapshot, peer)) return;
            var checkpoint = snapshot.MovementCheckpoint;
            System.Func<bool> install = null;
            if (RejectMovementAck(checkpoint == null, "checkpoint-missing", snapshot, peer) ||
                RejectMovementAck(!checkpoint.Precedes(lease), "checkpoint-source-lease", snapshot, peer) ||
                RejectMovementAck(!movement.TryPrepare(checkpoint, checkpoint.SourceLease, checkpoint.Ticks, out install), "movement-prepare", snapshot, peer) ||
                RejectMovementAck(clock == null, "clock-unavailable", snapshot, peer) ||
                RejectMovementAck(!clock.TryApplyAuthorityHandoffBaseline(checkpoint.Ticks), "clock-baseline", snapshot, peer) ||
                RejectMovementAck(!install(), "movement-install", snapshot, peer))
            {
                Logger.Warning("Rejected paused lease epoch {Epoch}; final movement baseline is missing or incomplete", lease.Epoch);
                return;
            }
        }
        if (lease.IsPaused && !CampaignApplyCompletion.For(sessionIdentity).CanAcknowledgeThrough(lease, out var finalApplyReason))
        {
            Logger.Warning("Refused paused lease commit at epoch {Epoch}: {ApplyReason}", lease.Epoch, finalApplyReason);
            return;
        }
        if (!sessionIdentity.TryApply(lease, snapshot.RoomHostControllerId))
        {
            LogMovementAckStage("snapshot-rejected-session-apply", snapshot, peer);
            Logger.Warning("Rejected campaign authority lease snapshot for room {RoomId} at epoch {Epoch} " +
                "with room host {RoomHostControllerId}; current room is {CurrentRoomId} at epoch {CurrentEpoch} " +
                "with room host {CurrentRoomHostControllerId}",
                lease.RoomId, lease.Epoch, snapshot.RoomHostControllerId,
                sessionIdentity.RoomId, sessionIdentity.Epoch, sessionIdentity.RoomHostControllerId);
            return;
        }

        if (lease.IsPaused && movement != null && network != null && controller != null)
        {
            LogMovementAckStage("send-call-delivery-unconfirmed", snapshot, peer);
            network.SendAll(new NetworkCampaignMovementBaselineApplied(lease, controller.ControllerId, snapshot.MovementCheckpoint));
            LogMovementAckStage("send-returned-delivery-unconfirmed", snapshot, peer);
            network.FlushPendingMessages();
            LogMovementAckStage("flush-returned-delivery-unconfirmed", snapshot, peer);
        }

        else if (lease.IsPaused)
        {
            if (movement == null) LogMovementAckStage("send-skipped-missing-movement", snapshot, peer);
            if (network == null) LogMovementAckStage("send-skipped-missing-network", snapshot, peer);
            if (controller == null) LogMovementAckStage("send-skipped-missing-controller", snapshot, peer);
        }

        Logger.Information("Applied campaign authority lease for room {RoomId}, authority {AuthorityId}, " +
            "controller {ControllerId}, epoch {Epoch}",
            lease.RoomId, lease.AuthorityId, lease.AuthorityControllerId, lease.Epoch);
    }
    private bool RejectMovementAck(bool rejected, string reason, NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        if (rejected)
        {
            LogMovementAckStage("snapshot-rejected-" + reason, snapshot, peer);
            if (reason == "clock-baseline" || reason == "movement-install")
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(sessionIdentity.CurrentLease,
                    "Paused lease checkpoint installation refused at " + reason);
        }
        return rejected;
    }

    private void LogMovementAckStage(string stage, NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        string transport = DescribeMovementAckTransport(peer);
        Logger.Information("Movement baseline ACK client stage {Stage} room {RoomId} authority {AuthorityId} epoch {Epoch} controller {ControllerId} roomHost {RoomHostControllerId} ticks {Ticks} sha256 {Sha256} sourceEpoch {SourceEpoch} sourceWriter {SourceWriter} peerId {PeerId} peerRef {PeerReference} endpoint {Endpoint} state {PeerState} thread {ThreadId} handlerRef {HandlerReference} networkRef {NetworkReference} transportSnapshot {TransportSnapshot}; delivery unconfirmed",
            stage, snapshot.RoomId, snapshot.AuthorityId, snapshot.Epoch, controller?.ControllerId, snapshot.RoomHostControllerId,
            snapshot.MovementCheckpoint?.Ticks, snapshot.MovementCheckpoint?.Sha256, snapshot.MovementCheckpoint?.SourceLease.Epoch,
            snapshot.MovementCheckpoint?.SourceLease.AuthorityControllerId, peer?.Id, peer == null ? 0 : RuntimeHelpers.GetHashCode(peer),
            peer?.ToString(), peer?.ConnectionState, Thread.CurrentThread.ManagedThreadId, RuntimeHelpers.GetHashCode(this),
            network == null ? 0 : RuntimeHelpers.GetHashCode(network), transport);
    }

    private string DescribeMovementAckTransport(NetPeer peer)
    {
        try
        {
            string peers = network is CoopClient client ? client.DescribeRoomPeers() : "Unavailable(non-CoopClient transport)";
            int? queued = peer?.GetPacketsCountInReliableQueue(0, true);
            return $"{peers}; snapshotSenderOutboundChannel0OrderedPackets={queued?.ToString() ?? "Unavailable(non-peer sender)"}";
        }
        catch (System.Exception error)
        {
            Logger.Error(error, "Movement baseline ACK transport diagnostic read failed; original send/apply path continues");
            return "Unavailable(transport diagnostic read failed)";
        }
    }

}
