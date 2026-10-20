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

    private sealed class Installation
    {
        internal AuthorityLease Source, Target;
        internal long Ticks; internal string Hash;
        internal bool Installed, AckAttempted, MutationStarted;
    }
    private Installation installation;
    private sealed class DeferredSnapshot
    {
        internal NetworkAuthorityLeaseSnapshot Snapshot; internal NetPeer Peer;
    }
    private DeferredSnapshot deferredSnapshot;
    private int continuationQueued, disposed;

    private void DeferSnapshot(NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        Volatile.Write(ref deferredSnapshot, new DeferredSnapshot { Snapshot = snapshot, Peer = peer });
        if (CampaignApplyCompletion.For(sessionIdentity).GetStatusThrough(snapshot.ToLease(), out _) != CampaignApplyStatus.Pending)
            OnApplyProgressed(); // Close the completion-before-subscription race.
    }
    private void OnApplyProgressed()
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref deferredSnapshot) == null ||
            Interlocked.Exchange(ref continuationQueued, 1) != 0) return;
        GameThread.EnqueueSafe(() =>
        {
            Interlocked.Exchange(ref continuationQueued, 0);
            if (Volatile.Read(ref disposed) != 0) return;
            var pending = Interlocked.Exchange(ref deferredSnapshot, null);
            if (pending != null) ApplySnapshotSafely(pending.Snapshot, pending.Peer);
        }, context: nameof(AuthorityLeaseSnapshotHandler));
    }
    private bool CanApplyFromRoomPeer(NetPeer peer) => Volatile.Read(ref disposed) == 0 && peer != null &&
        network is IRoomHostPeerProvider provider && provider.IsCurrentRoomHostPeer(peer);
    private bool RequireCurrentSnapshotPeer(NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        if (CanApplyFromRoomPeer(peer)) return true;
        if (installation?.MutationStarted == true && installation.Target == snapshot.ToLease())
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(installation.Source, "Room host peer or service lifetime changed during final snapshot installation.");
        return false;
    }
    private void ApplySnapshotSafely(NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        if (!RequireCurrentSnapshotPeer(snapshot, peer)) return;
        try { ApplySnapshotOnGameThread(snapshot, peer); }
        catch (System.Exception ex)
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(snapshot.MovementCheckpoint?.SourceLease ?? default,
                "Paused lease checkpoint installation threw: " + ex);
            throw;
        }
    }

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
        CampaignApplyCompletion.For(sessionIdentity).Progressed += OnApplyProgressed;
        messageBroker.Subscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        Interlocked.Exchange(ref deferredSnapshot, null);
        CampaignApplyCompletion.For(sessionIdentity).Progressed -= OnApplyProgressed;
        messageBroker.Unsubscribe<NetworkAuthorityLeaseSnapshot>(Handle_AuthorityLeaseSnapshot);
    }

    private void Handle_AuthorityLeaseSnapshot(MessagePayload<NetworkAuthorityLeaseSnapshot> payload)
    {
        var snapshot = payload.What;
        var peer = payload.Who as NetPeer;
        if (!CanApplyFromRoomPeer(peer)) return;
        LogMovementAckStage("snapshot-ingress", snapshot, peer);
        GameThread.EnqueueSafe(() => ApplySnapshotSafely(snapshot, peer), context: nameof(AuthorityLeaseSnapshotHandler));
        LogMovementAckStage("snapshot-queued", snapshot, peer);
    }

    private void ApplySnapshotOnGameThread(NetworkAuthorityLeaseSnapshot snapshot, NetPeer peer)
    {
        var lease = snapshot.ToLease();
        LogMovementAckStage("snapshot-game-thread", snapshot, peer);
        var completion = CampaignApplyCompletion.For(sessionIdentity);
        if (!lease.IsValid || (sessionIdentity.TryGetConfirmedLease(out var known) &&
            (known.RoomId != lease.RoomId || lease.Epoch < known.Epoch || (known.Epoch == lease.Epoch && known != lease))) ||
            (sessionIdentity.RoomHostControllerId?.Length > 0 && sessionIdentity.RoomHostControllerId != snapshot.RoomHostControllerId)) return;
        var status = completion.GetStatusThrough(lease, out var applyReason);
        if (status != CampaignApplyStatus.Ready)
        {
            Logger.Warning("Deferred/refused lease snapshot at epoch {Epoch}: {ApplyReason}", lease.Epoch, applyReason);
            if (status == CampaignApplyStatus.Pending) DeferSnapshot(snapshot, peer);
            return;
        }
        if (lease.IsPaused && movement != null)
        {
            var checkpoint = snapshot.MovementCheckpoint;
            if (RejectMovementAck(checkpoint == null, "checkpoint-missing", snapshot, peer) ||
                RejectMovementAck(!checkpoint.Precedes(lease), "checkpoint-source-lease", snapshot, peer)) return;
            if (!sessionIdentity.TryGetConfirmedLease(out var current) ||
                (current != checkpoint.SourceLease && current != lease) ||
                (sessionIdentity.RoomHostControllerId?.Length > 0 && sessionIdentity.RoomHostControllerId != snapshot.RoomHostControllerId))
            {
                LogMovementAckStage("snapshot-rejected-stale-or-conflicting-lease", snapshot, peer);
                return;
            }
            if (installation != null && installation.Target == lease &&
                (installation.Source != checkpoint.SourceLease || installation.Ticks != checkpoint.Ticks || installation.Hash != checkpoint.Sha256))
            {
                completion.RecordFailure(checkpoint.SourceLease, "Conflicting final movement snapshot for the same paused lease.");
                return;
            }
            if (installation == null || installation.Target != lease)
            {
                if (current != checkpoint.SourceLease) return;
                installation = new Installation { Source = checkpoint.SourceLease, Target = lease, Ticks = checkpoint.Ticks, Hash = checkpoint.Sha256 };
            }
            if (!installation.Installed)
            {
                if (RejectMovementAck(asyncTasks == null, "async-drain-unavailable", snapshot, peer) ||
                    RejectMovementAck(!asyncTasks.TryDrain(), "async-drain-incomplete", snapshot, peer)) return;
                System.Func<bool> install = null;
                if (RejectMovementAck(!movement.TryPrepare(checkpoint, checkpoint.SourceLease, checkpoint.Ticks, out install), "movement-prepare", snapshot, peer) ||
                    RejectMovementAck(clock == null, "clock-unavailable", snapshot, peer)) return;
                using (completion.ObserveSource(typeof(NetworkAuthorityLeaseSnapshot), checkpoint.SourceLease))
                using (CampaignExecutionScope.Enter(CampaignExecutionPhase.CheckpointInstall))
                {
                    installation.MutationStarted = true;
                    if (RejectMovementAck(!clock.TryApplyAuthorityHandoffBaseline(checkpoint.Ticks), "clock-baseline", snapshot, peer)) return;
                    if (!RequireCurrentSnapshotPeer(snapshot, peer)) return;
                    if (RejectMovementAck(!install(), "movement-install", snapshot, peer)) return;
                    installation.Installed = true;
                }
            }
            if (!completion.CanAcknowledgeThrough(lease, out applyReason))
            {
                Logger.Warning("Deferred paused lease commit at epoch {Epoch}: {ApplyReason}", lease.Epoch, applyReason);
                if (completion.GetStatusThrough(lease, out _) == CampaignApplyStatus.Pending) DeferSnapshot(snapshot, peer);
                return;
            }
        }
        if (!RequireCurrentSnapshotPeer(snapshot, peer)) return;
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
            if (!RequireCurrentSnapshotPeer(snapshot, peer) || installation.AckAttempted) return;
            installation.AckAttempted = true;
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
                CampaignApplyCompletion.For(sessionIdentity).RecordFailure(snapshot.MovementCheckpoint?.SourceLease ?? default,
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
