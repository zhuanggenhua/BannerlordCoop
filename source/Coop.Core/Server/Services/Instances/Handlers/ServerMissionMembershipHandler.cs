using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Coop.Core.Common.Session;
using GameInterface.Services.Entity;
using GameInterface.Services.Missions;
using GameInterface.Services.Players;
using LiteNetLib;
using Missions.Messages;
using Serilog;
using System.Globalization;
using System.Net;

namespace Coop.Core.Server.Services.Instances.Handlers;

/// <summary>
/// Tracks relay membership, introduces both sides with <see cref="NetworkMissionPeerEntered"/>, and fans
/// graceful or disconnected departures to the remaining mission members.
/// </summary>
public class ServerMissionMembershipHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerMissionMembershipHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IMissionManager missionManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly ISessionTunnelIdentityResolver tunnelIdentityResolver;
    private readonly IControllerIdProvider controllerIdProvider;

    public ServerMissionMembershipHandler(
        IMessageBroker messageBroker,
        IMissionManager missionManager,
        INetwork network,
        IPlayerManager playerManager)
        : this(messageBroker, missionManager, network, playerManager, null)
    {
    }

    public ServerMissionMembershipHandler(
        IMessageBroker messageBroker,
        IMissionManager missionManager,
        INetwork network,
        IPlayerManager playerManager,
        ISessionTunnelIdentityResolver tunnelIdentityResolver)
        : this(messageBroker, missionManager, network, playerManager, tunnelIdentityResolver, null) { }

    public ServerMissionMembershipHandler(
        IMessageBroker messageBroker,
        IMissionManager missionManager,
        INetwork network,
        IPlayerManager playerManager,
        ISessionTunnelIdentityResolver tunnelIdentityResolver,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.missionManager = missionManager;
        this.network = network;
        this.playerManager = playerManager;
        this.tunnelIdentityResolver = tunnelIdentityResolver;
        this.controllerIdProvider = controllerIdProvider;

        messageBroker.Subscribe<NetworkRequestMissionIntroduction>(Handle_RequestMissionIntroduction);
        messageBroker.Subscribe<NetworkMissionEntered>(Handle_MissionEntered);
        messageBroker.Subscribe<NetworkMissionLeft>(Handle_MissionLeft);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkRequestMissionIntroduction>(Handle_RequestMissionIntroduction);
        messageBroker.Unsubscribe<NetworkMissionEntered>(Handle_MissionEntered);
        messageBroker.Unsubscribe<NetworkMissionLeft>(Handle_MissionLeft);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    private void Handle_RequestMissionIntroduction(MessagePayload<NetworkRequestMissionIntroduction> payload)
    {
        var request = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!TryResolveEndpoint(payload.Who, out var endpoint)) return;
            string token;
            bool authorized = endpoint is LocalPlayerEndpoint local
                ? missionManager.TryAuthorizeIntroduction(local, request.InstanceId, request.RequestId, out token)
                : missionManager.TryAuthorizeIntroduction(endpoint.Peer, endpoint.ControllerId, request.InstanceId, request.RequestId, out token);
            if (authorized) Send(endpoint, new NetworkMissionIntroductionAuthorized(request.InstanceId, request.RequestId, token));
        }, context: nameof(Handle_RequestMissionIntroduction));
    }

    private void Handle_MissionEntered(MessagePayload<NetworkMissionEntered> payload)
    {
        var message = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!TryResolveEndpoint(payload.Who, out var endpoint)) return;
            MissionEntryResult result;
            bool entered = endpoint is LocalPlayerEndpoint local
                ? missionManager.TryEnterMission(local, message.InstanceId, out result)
                : missionManager.TryEnterMission(endpoint.Peer, endpoint.ControllerId, message.InstanceId, out result);
            if (!entered) return;

            // Credential precedes peer introductions on the same remote connection or local dispatch.
            Send(endpoint, new NetworkMissionCredentialIssued(result.InstanceId, result.PeerCredential));
            if (result.Status != MissionEntryStatus.Unchanged)
            {
                foreach (var departure in result.PreviousDepartures)
                    PublishDeparture(departure, wasRetreat: true);
                messageBroker.Publish(this,
                    new MissionMemberEntered(result.ControllerId, result.InstanceId, result.IsFirstMember));
            }

            var newcomerSteamId = ResolveSteamId(endpoint.Peer, result.ControllerId);
            foreach (var (otherEndpoint, otherPeerCredential) in result.ExistingEndpoints)
            {
                var existingSteamId = ResolveSteamId(otherEndpoint.Peer, otherEndpoint.ControllerId);
                Send(otherEndpoint, new NetworkMissionPeerEntered(
                    result.ControllerId, result.InstanceId, newcomerSteamId, result.PeerCredential));
                Send(endpoint, new NetworkMissionPeerEntered(
                    otherEndpoint.ControllerId, result.InstanceId, existingSteamId, otherPeerCredential));
            }
        }, context: nameof(Handle_MissionEntered));
    }

    private bool TryResolveEndpoint(object source, out MissionPlayerEndpoint endpoint)
    {
        endpoint = null;
        if (source is NetPeer peer && TryGetCurrentController(peer, out var remoteController))
        {
            endpoint = new RemotePeerEndpoint(remoteController, peer);
            return true;
        }
        if (source is LocalPlayerEndpoint local && ModInformation.IsListenAuthority &&
            controllerIdProvider != null && local.ControllerId == controllerIdProvider.ControllerId &&
            playerManager.TryGetPlayer(local.ControllerId, out _))
        {
            endpoint = local;
            return true;
        }
        return false;
    }

    private void Send<T>(MissionPlayerEndpoint endpoint, T message) where T : IMessage
    {
        if (endpoint is LocalPlayerEndpoint) messageBroker.Publish(this, message);
        else network.Send(endpoint.Peer, message);
    }

    private ulong ResolveSteamId(NetPeer peer, string controllerId)
    {
        if (peer == null) return 0;
        var endpoint = new IPEndPoint(peer.Address, peer.Port);
        if (tunnelIdentityResolver != null
            && tunnelIdentityResolver.TryGetRemoteSteamId(endpoint, out var steamId))
            return steamId;

        // The hosting client reaches its spawned server directly over loopback, so it has no tunnel
        // endpoint to map. In Release its controller id is its Steam id; constrain that fallback to a
        // managed server's local peer so arbitrary direct-IP controller ids are never treated as Steam.
        if (ManagedServerConfig.IsManagedServer
            && IPAddress.IsLoopback(peer.Address)
            && ulong.TryParse(controllerId, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
        {
            return steamId;
        }

        return 0;
    }

    private void Handle_MissionLeft(MessagePayload<NetworkMissionLeft> payload)
    {
        if (payload.Who is NetPeer peer && TryGetCurrentController(peer, out _)) missionManager.RevokeRelay(peer);
        var message = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!TryResolveEndpoint(payload.Who, out var endpoint)) return;
            MissionDeparture departure;
            bool left = endpoint is LocalPlayerEndpoint local
                ? missionManager.TryLeaveMission(local, message.InstanceId, out departure)
                : missionManager.TryLeaveMission(endpoint.Peer, endpoint.ControllerId, message.InstanceId, out departure);
            if (left) PublishDeparture(departure, wasRetreat: true);
        }, context: nameof(Handle_MissionLeft));
    }

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        var peer = payload.What.PlayerId;
        missionManager.RevokeRelay(peer);

        GameThread.RunSafe(() =>
        {
            var departures = missionManager.HandleDisconnect(peer);
            foreach (var departure in departures)
                PublishDeparture(departure, wasRetreat: false);
        }, context: nameof(Handle_PlayerDisconnected));
    }

    private bool TryGetCurrentController(NetPeer peer, out string controllerId)
    {
        controllerId = null;
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !playerManager.TryGetPeer(player.ControllerId, out var currentPeer) ||
            !object.ReferenceEquals(currentPeer, peer))
        {
            return false;
        }

        controllerId = player.ControllerId;
        return true;
    }

    private void PublishDeparture(MissionDeparture departure, bool wasRetreat)
    {
        foreach (var endpoint in departure.RemainingEndpoints)
        {
            if (wasRetreat)
                Send(endpoint, new MissionPeerLeft(departure.ControllerId, departure.InstanceId));
            else
                Send(endpoint, new MissionPeerDisconnected(departure.ControllerId, departure.InstanceId));
        }

        messageBroker.Publish(this, new MissionMemberDeparted(
            departure.ControllerId,
            departure.InstanceId,
            wasRetreat,
            departure.IsInstanceEmpty));
    }
}
