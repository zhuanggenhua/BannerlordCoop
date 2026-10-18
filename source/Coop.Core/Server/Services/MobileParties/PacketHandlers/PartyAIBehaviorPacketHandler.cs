using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.PacketHandlers;
using Coop.Core.Server.Services.MobileParties.Messages;
using Coop.Core.Server.Services.MobileParties.Packets;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using LiteNetLib;
using GameInterface.Services.Players;
using GameInterface.Services.CampaignAuthority;
using TaleWorlds.CampaignSystem.Party;
using static GameInterface.Services.ObjectManager.ObjectManager;
using System.Collections.Generic;

namespace Coop.Core.Server.Services.MobileParties.PacketHandlers;

/// <summary>
/// Handles incoming <see cref="RequestMobilePartyBehaviorPacket"/>
/// </summary>
internal class RequestMobilePartyBehaviorPacketHandler : IPacketHandler
{
    // Coalescer channel for per-party behavior updates; only the latest behavior per party is sent each tick.
    private const string PartyBehaviorUpdateChannel = "PartyBehaviorUpdate";

    public PacketType PacketType => PacketType.RequestUpdatePartyBehavior;

    private readonly IPacketManager packetManager;
    private readonly ISendCoalescer coalescer;
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IPartyBehaviorWireMapper wireMapper;
    private readonly IPlayerManager players;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly object authorityCommandGate = new object();
    private readonly Dictionary<NetPeer, long> lastRequestSequenceByPeer = new Dictionary<NetPeer, long>();
    private AuthorityLease authorityCommandLease;
    private long nextAuthorityCommandSequence;

    public RequestMobilePartyBehaviorPacketHandler(
        IPacketManager packetManager,
        ISendCoalescer coalescer,
        IMessageBroker messageBroker,
        INetwork network,
        IPartyBehaviorWireMapper wireMapper,
        IPlayerManager players,
        ICampaignAuthorityContext authorityContext = null)
    {
        this.packetManager = packetManager;
        this.coalescer = coalescer;
        this.messageBroker = messageBroker;
        this.network = network;
        this.wireMapper = wireMapper;
        this.players = players;
        this.authorityContext = authorityContext;
        packetManager.RegisterPacketHandler(this);

        messageBroker.Subscribe<PartyBehaviorUpdated>(Handle_PartyBehaviorUpdated);
        messageBroker.Subscribe<ControlledPartyBehaviorUpdated>(Handle_ControlledPartyBehaviorUpdated);
    }

    public void Dispose()
    {
        packetManager.RemovePacketHandler(this);

        messageBroker.Unsubscribe<PartyBehaviorUpdated>(Handle_PartyBehaviorUpdated);
        messageBroker.Unsubscribe<ControlledPartyBehaviorUpdated>(Handle_ControlledPartyBehaviorUpdated);
        lock (authorityCommandGate)
        {
            lastRequestSequenceByPeer.Clear();
            nextAuthorityCommandSequence = 0;
            authorityCommandLease = default;
        }
    }

    public void HandlePacket(NetPeer peer, IPacket packet)
    {
        RequestMobilePartyBehaviorPacket convertedPacket = (RequestMobilePartyBehaviorPacket)packet;

        GameThread.RunSafe(() =>
        {
            if (!TryGetRequestLease(convertedPacket, out var lease)) return;
            if (!wireMapper.TryFromNetwork(convertedPacket.BehaviorUpdateData, out var data)) return;
            if (!TryAuthorizeOwnership(peer, ref data)) return;
            if (!TryAdvanceRequestSequence(peer, convertedPacket.RequestSequence)) return;

            if (authorityContext?.IsAuthorityKnown == true)
            {
                bool localAuthority = authorityContext.TryGetLocalAuthorityLease(out var localLease) &&
                    localLease == lease;
                if (localAuthority && lease.IsValid && !lease.IsPaused)
                {
                    messageBroker.Publish(this, new UpdatePartyBehavior(ref data, alreadyOnGameThread: true));
                    return;
                }

                if (lease.IsValid && !lease.IsPaused && !localAuthority)
                    RouteToRemoteAuthority(data, lease);
                return;
            }

            messageBroker.Publish(this, new UpdatePartyBehavior(ref data, alreadyOnGameThread: true));
        }, context: nameof(RequestMobilePartyBehaviorPacketHandler));
    }

    internal bool TryGetRequestLease(RequestMobilePartyBehaviorPacket packet, out AuthorityLease lease)
    {
        if (authorityContext?.IsAuthorityKnown != true)
        {
            lease = default;
            return true;
        }

        return authorityContext.TryGetCurrentLease(out lease) && packet.MatchesLease(lease);
    }

    internal bool TryAuthorize(NetPeer peer, ref PartyBehaviorUpdateData data)
    {
        // Once a confirmed lease is remote or paused, the room host may relay traffic but it
        // must not execute a player's world command under its stale process role.
        if (authorityContext?.IsAuthorityKnown == true &&
            !authorityContext.TryGetLocalAuthorityLease(out var localLease))
            return false;

        return TryAuthorizeOwnership(peer, ref data);
    }

    private bool TryAuthorizeOwnership(NetPeer peer, ref PartyBehaviorUpdateData data)
    {
        if (peer == null || !players.TryGetPlayer(peer, out var player) || player == null ||
            !players.TryGetPeer(player.ControllerId, out var currentPeer) ||
            !ReferenceEquals(currentPeer, peer) || player.MobilePartyId != data.MobilePartyId ||
            data.ForcePosition)
            return false;
        data.OriginControllerId = player.ControllerId;
        return true;
    }

    private bool TryAdvanceRequestSequence(NetPeer peer, long requestSequence)
    {
        if (requestSequence <= 0) return authorityContext?.IsAuthorityKnown != true;

        lock (authorityCommandGate)
        {
            if (lastRequestSequenceByPeer.TryGetValue(peer, out var lastSequence) &&
                requestSequence <= lastSequence)
                return false;

            lastRequestSequenceByPeer[peer] = requestSequence;
            return true;
        }
    }

    internal void Handle_ControlledPartyBehaviorUpdated(MessagePayload<ControlledPartyBehaviorUpdated> payload)
    {
        if (authorityContext?.IsAuthorityKnown != true ||
            authorityContext.TryGetLocalAuthorityLease(out var lease) ||
            !lease.IsValid || lease.IsPaused)
            return;

        var data = payload.What.BehaviorUpdateData;
        if (data.ForcePosition || string.IsNullOrWhiteSpace(data.OriginControllerId) ||
            !players.TryGetPlayer(data.OriginControllerId, out var player) || player == null ||
            Compact(player.MobilePartyId, typeof(MobileParty)) != Compact(data.MobilePartyId, typeof(MobileParty)))
            return;

        RouteToRemoteAuthority(data, lease);
    }

    internal bool RouteToRemoteAuthority(PartyBehaviorUpdateData data, AuthorityLease lease)
    {
        if (!players.TryGetPeer(lease.AuthorityControllerId, out var authorityPeer) || authorityPeer == null ||
            !wireMapper.TryToNetwork(data, out var networkData))
            return false;

        long sequence;
        lock (authorityCommandGate)
        {
            if (authorityCommandLease != lease)
            {
                authorityCommandLease = lease;
                nextAuthorityCommandSequence = 0;
            }

            if (nextAuthorityCommandSequence == long.MaxValue)
                throw new System.InvalidOperationException("Campaign authority command sequence space is exhausted.");

            sequence = ++nextAuthorityCommandSequence;
        }

        network.Send(authorityPeer, new NetworkCampaignAuthorityPartyBehaviorCommand(
            lease,
            sequence,
            data.OriginControllerId,
            networkData));
        return true;
    }

    private void Handle_PartyBehaviorUpdated(MessagePayload<PartyBehaviorUpdated> payload)
    {
        var data = payload.What.BehaviorUpdateData;

        // Coalesce per party: repeated behavior changes to one party collapse into a single latest-wins send per tick.
        if (!wireMapper.TryToNetwork(data, out var networkData)) return;
        var key = new CoalesceKey(PartyBehaviorUpdateChannel, networkData.MobilePartyId);

        if (authorityContext?.IsAuthorityKnown == true)
        {
            if (!authorityContext.TryGetLocalAuthorityLease(out var lease) || !lease.IsValid)
                return;

            coalescer.Enqueue(
                key,
                new LatestWinsPayload(new NetworkCampaignAuthorityPartyBehaviorDelta(networkData)));
            if (data.ForcePosition)
                coalescer.FlushInstance(key.InstanceId, network);
            return;
        }

        coalescer.Enqueue(key, new LatestWinsPayload(new NetworkUpdatePartyBehavior(networkData)));

        if (data.ForcePosition)
            coalescer.FlushInstance(key.InstanceId, network);
    }
}
