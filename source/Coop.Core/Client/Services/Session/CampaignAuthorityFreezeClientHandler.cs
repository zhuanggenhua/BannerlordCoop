using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Session;
using GameInterface.Services.Entity;
using Serilog;

namespace Coop.Core.Client.Services.Session;

/// <summary>Stops a remote authority's local writes before it acknowledges a room-host handoff.</summary>
public sealed class CampaignAuthorityFreezeClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityFreezeClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider controllerIdProvider;

    public CampaignAuthorityFreezeClientHandler(
        IMessageBroker messageBroker,
        INetwork network,
        ISessionIdentity sessionIdentity,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        this.controllerIdProvider = controllerIdProvider;
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeRequest>(Handle_FreezeRequest);
        messageBroker.Subscribe<NetworkCampaignAuthorityFreezeCanceled>(Handle_FreezeCanceled);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeRequest>(Handle_FreezeRequest);
        messageBroker.Unsubscribe<NetworkCampaignAuthorityFreezeCanceled>(Handle_FreezeCanceled);
    }

    private void Handle_FreezeRequest(MessagePayload<NetworkCampaignAuthorityFreezeRequest> payload)
    {
        var request = payload.What;
        GameThread.EnqueueSafe(() => ApplyFreezeRequest(request), context: nameof(CampaignAuthorityFreezeClientHandler));
    }

    private void ApplyFreezeRequest(NetworkCampaignAuthorityFreezeRequest request)
    {
        if (!sessionIdentity.TryGetConfirmedLease(out var lease) ||
            !Matches(request, lease, controllerIdProvider.ControllerId) ||
            !sessionIdentity.TryFreezeLocalAuthority(lease, request.HandoffSequence))
        {
            Logger.Warning("Ignored campaign authority freeze request {HandoffSequence} for controller {ControllerId}; the request did not match the active lease",
                request.HandoffSequence, controllerIdProvider.ControllerId);
            return;
        }

        try
        {
            // Fence local simulation before flushing. The acknowledgement follows every already queued
            // authority delta on this reliable-ordered connection.
            network.FlushPendingMessages();
            network.SendAll(request.ToAcknowledged());
            network.FlushPendingMessages();
            Logger.Information("Froze local campaign writes and acknowledged handoff {HandoffSequence} at epoch {Epoch} to controller {CandidateControllerId}",
                request.HandoffSequence, request.Epoch, request.CandidateControllerId);
        }
        catch (System.Exception ex)
        {
            Logger.Error(ex, "Failed to acknowledge campaign authority freeze {HandoffSequence}",
                request.HandoffSequence);
        }
    }

    private void Handle_FreezeCanceled(MessagePayload<NetworkCampaignAuthorityFreezeCanceled> payload)
    {
        var canceled = payload.What;
        var lease = new AuthorityLease(
            canceled.RoomId, canceled.AuthorityId, canceled.AuthorityControllerId, canceled.Epoch);
        GameThread.EnqueueSafe(() =>
        {
            if (!string.Equals(canceled.AuthorityControllerId, controllerIdProvider.ControllerId, System.StringComparison.Ordinal) ||
                !sessionIdentity.TryCancelLocalAuthorityFreeze(lease, canceled.HandoffSequence))
                return;

            Logger.Information("Canceled campaign authority freeze {HandoffSequence}; epoch {Epoch} remains active",
                canceled.HandoffSequence, canceled.Epoch);
        }, context: nameof(CampaignAuthorityFreezeClientHandler));
    }

    internal static bool Matches(
        NetworkCampaignAuthorityFreezeRequest request,
        AuthorityLease currentLease,
        string localControllerId) =>
        request.HandoffSequence > 0 &&
        !string.IsNullOrWhiteSpace(request.CandidateControllerId) &&
        !string.Equals(request.CandidateControllerId, request.AuthorityControllerId, System.StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(localControllerId) &&
        currentLease.IsValid && !currentLease.IsPaused &&
        string.Equals(request.RoomId, currentLease.RoomId, System.StringComparison.Ordinal) &&
        string.Equals(request.AuthorityId, currentLease.AuthorityId, System.StringComparison.Ordinal) &&
        string.Equals(request.AuthorityControllerId, currentLease.AuthorityControllerId, System.StringComparison.Ordinal) &&
        request.Epoch == currentLease.Epoch &&
        string.Equals(request.AuthorityControllerId, localControllerId, System.StringComparison.Ordinal);
}
