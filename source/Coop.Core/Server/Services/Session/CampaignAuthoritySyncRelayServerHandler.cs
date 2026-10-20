using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;

namespace Coop.Core.Server.Services.Session;

/// <summary>Validates authority-originated AutoSync deltas, applies them to the relay, and forwards them.</summary>
public sealed class CampaignAuthoritySyncRelayServerHandler : IHandler
{
    private sealed class StreamWatermark
    {
        public string RoomId { get; set; } = string.Empty;
        public string AuthorityId { get; set; } = string.Empty;
        public string AuthorityControllerId { get; set; } = string.Empty;
        public long Epoch { get; set; }
        public long Sequence { get; set; }
    }

    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthoritySyncRelayServerHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IConnectionCollection connections;
    private readonly IPlayerManager playerManager;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly ICommonSerializer serializer;
    private readonly object gate = new object();
    private readonly Dictionary<NetPeer, StreamWatermark> watermarks = new Dictionary<NetPeer, StreamWatermark>();

    public CampaignAuthoritySyncRelayServerHandler(
        IMessageBroker messageBroker,
        IConnectionCollection connections,
        IPlayerManager playerManager,
        INetwork network,
        ISessionIdentity sessionIdentity,
        ICommonSerializer serializer)
    {
        this.messageBroker = messageBroker;
        this.connections = connections;
        this.playerManager = playerManager;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        CampaignApplyCompletion.Attach(messageBroker, sessionIdentity);
        this.serializer = serializer;
        messageBroker.Subscribe<NetworkCampaignAuthoritySyncEnvelope>(Handle_AuthoritySyncEnvelope);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkCampaignAuthoritySyncEnvelope>(Handle_AuthoritySyncEnvelope);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        lock (gate) watermarks.Clear();
    }

    private void Handle_AuthoritySyncEnvelope(MessagePayload<NetworkCampaignAuthoritySyncEnvelope> payload)
    {
        GameThread.EnqueueSafe(() => ApplyEnvelopeOnGameThread(payload), context: nameof(CampaignAuthoritySyncRelayServerHandler));
    }

    private void ApplyEnvelopeOnGameThread(MessagePayload<NetworkCampaignAuthoritySyncEnvelope> payload)
    {
        if (payload.Who is not NetPeer peer || !payload.What.IsValid ||
            !connections.HasCompletedCampaignSynchronization(peer) ||
            !playerManager.TryGetPlayer(peer, out Player player) ||
            !sessionIdentity.TryGetConfirmedLease(out var lease) || lease.IsPaused ||
            !MatchesCurrentAuthority(payload.What, lease, player.ControllerId))
        {
            Logger.Warning("Rejected campaign authority sync envelope from an unregistered, stale, or non-authority peer");
            return;
        }

        IMessage innerMessage;
        try
        {
            innerMessage = serializer.Deserialize<IMessage>(payload.What.Payload);
        }
        catch (Exception ex)
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Authority result deserialization failed: " + ex);
            Logger.Error(ex, "Could not deserialize campaign authority sync envelope {Sequence}", payload.What.Sequence);
            return;
        }

        if (innerMessage is not ICampaignAuthorityScopedMessage)
        {
            CampaignApplyCompletion.For(sessionIdentity).RecordFailure(lease, "Validated authority envelope contained an unclassified result.");
            Logger.Warning("Rejected campaign authority envelope {Sequence} containing non-authority message {MessageType}",
                payload.What.Sequence, innerMessage?.GetType());
            return;
        }

        if (!TryAdvanceWatermark(peer, payload.What))
        {
            Logger.Warning("Rejected replayed campaign authority sync envelope {Sequence} from controller {ControllerId}",
                payload.What.Sequence, player.ControllerId);
            return;
        }

        // AutoSync receive handlers marshal the actual object lookup and apply to the game thread.
        messageBroker.PublishRuntime(peer, innerMessage);
        network.SendAllBut(peer, payload.What);
    }

    private bool TryAdvanceWatermark(NetPeer peer, NetworkCampaignAuthoritySyncEnvelope envelope)
    {
        lock (gate)
        {
            if (!watermarks.TryGetValue(peer, out var watermark))
            {
                watermark = new StreamWatermark();
                watermarks.Add(peer, watermark);
            }

            if (!string.Equals(watermark.RoomId, envelope.RoomId, StringComparison.Ordinal) ||
                watermark.Epoch != envelope.Epoch ||
                !string.Equals(watermark.AuthorityControllerId, envelope.AuthorityControllerId, StringComparison.Ordinal) ||
                !string.Equals(watermark.AuthorityId, envelope.AuthorityId, StringComparison.Ordinal))
            {
                watermark.RoomId = envelope.RoomId;
                watermark.AuthorityId = envelope.AuthorityId;
                watermark.AuthorityControllerId = envelope.AuthorityControllerId;
                watermark.Epoch = envelope.Epoch;
                watermark.Sequence = 0;
            }

            if (envelope.Sequence <= watermark.Sequence) return false;
            watermark.Sequence = envelope.Sequence;
            return true;
        }
    }

    private static bool MatchesCurrentAuthority(
        NetworkCampaignAuthoritySyncEnvelope envelope,
        AuthorityLease lease,
        string peerControllerId) =>
        envelope.RoomId == lease.RoomId &&
        envelope.AuthorityId == lease.AuthorityId &&
        !string.IsNullOrWhiteSpace(envelope.AuthorityControllerId) &&
        envelope.AuthorityControllerId == lease.AuthorityControllerId &&
        envelope.Epoch == lease.Epoch &&
        envelope.AuthorityControllerId == peerControllerId;

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (payload.What.PlayerId is not NetPeer peer) return;
        lock (gate) watermarks.Remove(peer);
    }
}
