using Common.Messaging;

using LiteNetLib;

namespace Coop.Core.Client.Messages;

/// <summary>Reports whether the latest join baseline was applied in full.</summary>
public sealed class JoinCampaignBaselineApplied : IEvent
{
    public bool Success { get; }

    public string BaselineRequestId { get; }

    public AuthorityLease? PausedCheckpointLease { get; }

    public NetPeer SourcePeer { get; }

    public bool HasPausedCheckpointAttestation => PausedCheckpointLease.HasValue;

    public JoinCampaignBaselineApplied(
        bool success,
        string baselineRequestId = null,
        AuthorityLease? pausedCheckpointLease = null,
        NetPeer sourcePeer = null)
    {
        Success = success;
        BaselineRequestId = baselineRequestId ?? string.Empty;
        if (success && sourcePeer != null && pausedCheckpointLease is { IsValid: true, IsPaused: true } lease)
        {
            PausedCheckpointLease = lease;
            SourcePeer = sourcePeer;
        }
        else
        {
            PausedCheckpointLease = null;
            SourcePeer = null;
        }
    }
}
