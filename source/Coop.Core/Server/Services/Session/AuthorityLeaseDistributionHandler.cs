using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Messages;
using Coop.Core.Server.Connections.Messages;
using Serilog;

namespace Coop.Core.Server.Services.Session;

/// <summary>Sends the current campaign lease to each newly connected replica.</summary>
public sealed class AuthorityLeaseDistributionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<AuthorityLeaseDistributionHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly ISessionIdentity sessionIdentity;
    private readonly IControllerIdProvider controllerIdProvider;

    public AuthorityLeaseDistributionHandler(
        IMessageBroker messageBroker,
        INetwork network,
        ISessionIdentity sessionIdentity,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.sessionIdentity = sessionIdentity;
        this.controllerIdProvider = controllerIdProvider;

        messageBroker.Subscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Subscribe<PlayerConnected>(Handle_PlayerConnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(Handle_CampaignReady);
        messageBroker.Unsubscribe<PlayerConnected>(Handle_PlayerConnected);
    }

    private void Handle_CampaignReady(MessagePayload<CampaignReady> _)
    {
        InitializeLocalAuthorityController();
    }

    private void Handle_PlayerConnected(MessagePayload<PlayerConnected> payload)
    {
        if (!InitializeLocalAuthorityController()) return;
        var lease = sessionIdentity.CurrentLease;
        network.Send(payload.What.PlayerPeer, new NetworkAuthorityLeaseSnapshot(lease));
        Logger.Information("Sent campaign authority lease epoch {Epoch} for room {RoomId} to a joining player",
            lease.Epoch, lease.RoomId);
    }

    private bool InitializeLocalAuthorityController()
    {
        if (ModInformation.IsListenAuthority &&
            !sessionIdentity.TrySetAuthorityControllerId(controllerIdProvider.ControllerId))
        {
            Logger.Warning("Could not bind ListenAuthority controller {ControllerId} to authority lease epoch {Epoch}",
                controllerIdProvider.ControllerId, sessionIdentity.Epoch);
            return false;
        }

        if (!sessionIdentity.TryConfirmCurrentLease())
        {
            Logger.Error("Could not confirm campaign authority lease for room {RoomId} at epoch {Epoch}",
                sessionIdentity.RoomId, sessionIdentity.Epoch);
            return false;
        }

        return true;
    }
}
