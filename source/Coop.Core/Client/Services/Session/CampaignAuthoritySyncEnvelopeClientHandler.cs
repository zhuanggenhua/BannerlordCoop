using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network.Session;
using Common.Serialization;
using Common.Session;
using Serilog;
using System;

namespace Coop.Core.Client.Services.Session;

/// <summary>Applies relay-validated authority deltas only when they match the confirmed local lease.</summary>
public sealed class CampaignAuthoritySyncEnvelopeClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthoritySyncEnvelopeClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly ISessionIdentity sessionIdentity;
    private readonly ICommonSerializer serializer;
    private readonly object gate = new object();
    private string receivedRoomId = string.Empty;
    private string receivedAuthorityId = string.Empty;
    private string receivedAuthorityControllerId = string.Empty;
    private long receivedEpoch;
    private long receivedSequence;

    public CampaignAuthoritySyncEnvelopeClientHandler(
        IMessageBroker messageBroker,
        ISessionIdentity sessionIdentity,
        ICommonSerializer serializer)
    {
        this.messageBroker = messageBroker;
        this.sessionIdentity = sessionIdentity;
        this.serializer = serializer;
        messageBroker.Subscribe<NetworkCampaignAuthoritySyncEnvelope>(Handle_AuthoritySyncEnvelope);
    }

    public void Dispose() => messageBroker.Unsubscribe<NetworkCampaignAuthoritySyncEnvelope>(Handle_AuthoritySyncEnvelope);

    private void Handle_AuthoritySyncEnvelope(MessagePayload<NetworkCampaignAuthoritySyncEnvelope> payload)
    {
        GameThread.EnqueueSafe(() => ApplyEnvelopeOnGameThread(payload), context: nameof(CampaignAuthoritySyncEnvelopeClientHandler));
    }

    private void ApplyEnvelopeOnGameThread(MessagePayload<NetworkCampaignAuthoritySyncEnvelope> payload)
    {
        var envelope = payload.What;
        if (!envelope.IsValid || !sessionIdentity.TryGetConfirmedLease(out var lease) || lease.IsPaused ||
            !MatchesCurrentAuthority(envelope, lease))
        {
            Logger.Warning("Ignored campaign authority sync envelope {Sequence} at epoch {Epoch}; it did not match the confirmed lease",
                envelope.Sequence, envelope.Epoch);
            return;
        }

        IMessage innerMessage;
        try
        {
            innerMessage = serializer.Deserialize<IMessage>(envelope.Payload);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not deserialize campaign authority sync envelope {Sequence}", envelope.Sequence);
            return;
        }

        if (innerMessage is not ICampaignAuthorityScopedMessage)
        {
            Logger.Warning("Ignored campaign authority envelope {Sequence} containing non-authority message {MessageType}",
                envelope.Sequence, innerMessage?.GetType());
            return;
        }

        if (!TryAdvanceWatermark(envelope))
        {
            Logger.Warning("Ignored replayed campaign authority sync envelope {Sequence} at epoch {Epoch}",
                envelope.Sequence, envelope.Epoch);
            return;
        }

        // Lease snapshots and envelopes share this queue, so the preceding epoch finishes first.
        messageBroker.Publish(payload.Who, innerMessage);
    }

    private bool TryAdvanceWatermark(NetworkCampaignAuthoritySyncEnvelope envelope)
    {
        lock (gate)
        {
            if (!string.Equals(receivedRoomId, envelope.RoomId, StringComparison.Ordinal) ||
                receivedEpoch != envelope.Epoch ||
                !string.Equals(receivedAuthorityControllerId, envelope.AuthorityControllerId, StringComparison.Ordinal) ||
                !string.Equals(receivedAuthorityId, envelope.AuthorityId, StringComparison.Ordinal))
            {
                receivedRoomId = envelope.RoomId;
                receivedAuthorityId = envelope.AuthorityId;
                receivedAuthorityControllerId = envelope.AuthorityControllerId;
                receivedEpoch = envelope.Epoch;
                receivedSequence = 0;
            }

            if (envelope.Sequence <= receivedSequence) return false;
            receivedSequence = envelope.Sequence;
            return true;
        }
    }

    private static bool MatchesCurrentAuthority(NetworkCampaignAuthoritySyncEnvelope envelope, AuthorityLease lease) =>
        envelope.RoomId == lease.RoomId &&
        envelope.AuthorityId == lease.AuthorityId &&
        envelope.AuthorityControllerId == lease.AuthorityControllerId &&
        envelope.Epoch == lease.Epoch;
}
