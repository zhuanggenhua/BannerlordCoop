using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using Coop.Core.Client.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.Time.Messages;
using Serilog;
using System;

namespace Coop.Core.Client.Services.Session;

/// <summary>
/// Acknowledges an authority handoff barrier only after previously received ordered updates have
/// reached the game-thread apply queue and the local campaign is still safe to simulate.
/// </summary>
public sealed class CampaignAuthorityHandoffBarrierClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityHandoffBarrierClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider controllerIdProvider;
    private bool campaignSynchronized;
    private bool mapReady;

    public CampaignAuthorityHandoffBarrierClientHandler(
        IMessageBroker messageBroker,
        INetwork network,
        ISessionIdentity sessionIdentity,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        this.controllerIdProvider = controllerIdProvider;
        messageBroker.Subscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Subscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Subscribe<NetworkCampaignAuthorityHandoffBarrier>(Handle_HandoffBarrier);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<ClientCampaignReady>(Handle_ClientCampaignReady);
        messageBroker.Unsubscribe<CampaignMapSimulationReadinessChanged>(Handle_MapReadinessChanged);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityHandoffBarrier>(Handle_HandoffBarrier);
    }

    private void Handle_ClientCampaignReady(MessagePayload<ClientCampaignReady> _)
    {
        campaignSynchronized = true;
    }

    private void Handle_MapReadinessChanged(MessagePayload<CampaignMapSimulationReadinessChanged> payload)
    {
        mapReady = payload.What.IsMapReady;
    }

    private void Handle_HandoffBarrier(MessagePayload<NetworkCampaignAuthorityHandoffBarrier> payload)
    {
        var barrier = payload.What;

        // All receive-side campaign applies that defer to GameThread.EnqueueSafe are ahead of this
        // acknowledgement. Reading session and campaign readiness here also avoids polling-thread reads.
        GameThread.EnqueueSafe(() => ApplyBarrierOnGameThread(barrier), context: nameof(CampaignAuthorityHandoffBarrierClientHandler));
    }

    private void ApplyBarrierOnGameThread(NetworkCampaignAuthorityHandoffBarrier barrier)
    {
        if (!campaignSynchronized || !mapReady ||
            !sessionIdentity.TryGetConfirmedLease(out var lease) || !lease.IsPaused ||
            !Matches(barrier, lease, controllerIdProvider.ControllerId))
        {
            Logger.Warning("Ignored campaign authority handoff barrier {BarrierSequence} for controller {ControllerId}; " +
                "campaign sync, map readiness, or paused lease did not match",
                barrier.BarrierSequence, controllerIdProvider.ControllerId);
            return;
        }

        network.SendAll(barrier.ToApplied());
        network.FlushPendingMessages();
        Logger.Information("Applied campaign authority handoff barrier {BarrierSequence} for paused epoch {Epoch}",
            barrier.BarrierSequence, barrier.PausedEpoch);
    }

    internal static bool Matches(
        NetworkCampaignAuthorityHandoffBarrier barrier,
        AuthorityLease lease,
        string localControllerId) =>
        lease.IsValid && lease.IsPaused &&
        !string.IsNullOrWhiteSpace(localControllerId) &&
        barrier.BarrierSequence > 0 && barrier.PausedEpoch == lease.Epoch &&
        string.Equals(barrier.RoomId, lease.RoomId, StringComparison.Ordinal) &&
        string.Equals(barrier.PausedAuthorityId, lease.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(barrier.CandidateControllerId, localControllerId, StringComparison.Ordinal);
}
