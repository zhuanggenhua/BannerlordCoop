using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Network.Session;
using Coop.Core.Server.Connections.Messages;
using GameInterface.Services.Missions;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System.Collections.Generic;
using System.Threading;

namespace Coop.Core.Server.Services.Instances.Handlers;

public interface IRoomMissionFactsPublisher
{
    bool TrySendInitial(NetPeer peer, out RoomMissionFactsVersion version);
    bool HasApplied(NetPeer peer, RoomMissionFactsVersion version);
}

public sealed class ServerRoomMissionFactsHandler : IHandler, IRoomMissionFactsPublisher
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerRoomMissionFactsHandler>();
    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly IRoomMissionFactsView facts;
    private readonly IPlayerManager players;
    private readonly Dictionary<NetPeer, RoomMissionFactsVersion> applied = new();
    private int disposed;

    public ServerRoomMissionFactsHandler(IMessageBroker broker, INetwork network,
        IRoomMissionFactsView facts, IPlayerManager players)
    {
        this.broker = broker;
        this.network = network;
        this.facts = facts;
        this.players = players;
        facts.FactsChanged += OnFactsChanged;
        broker.Subscribe<PlayerCampaignSynchronized>(Handle_Synchronized);
        broker.Subscribe<NetworkRoomMissionFactsApplied>(Handle_Applied);
        broker.Subscribe<PlayerDisconnected>(Handle_Disconnected);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        facts.FactsChanged -= OnFactsChanged;
        broker.Unsubscribe<PlayerCampaignSynchronized>(Handle_Synchronized);
        broker.Unsubscribe<NetworkRoomMissionFactsApplied>(Handle_Applied);
        broker.Unsubscribe<PlayerDisconnected>(Handle_Disconnected);
        lock (applied) applied.Clear();
    }

    public bool TrySendInitial(NetPeer peer, out RoomMissionFactsVersion version)
    {
        version = default;
        if (Volatile.Read(ref disposed) != 0 || peer == null || !facts.TryCapture(out var snapshot)) return false;
        network.SendImmediate(peer, new NetworkRoomMissionFactsSnapshot(snapshot));
        version = snapshot.Version;
        return true;
    }

    public bool HasApplied(NetPeer peer, RoomMissionFactsVersion version)
    {
        if (!IsCurrent(peer) || !facts.HasApplied(version)) return false;
        lock (applied) return applied.TryGetValue(peer, out var known) && known == version;
    }

    private bool IsCurrent(NetPeer peer) => Volatile.Read(ref disposed) == 0 && peer != null &&
        players.TryGetPlayer(peer, out var player) && players.TryGetPeer(player.ControllerId, out var current) &&
        ReferenceEquals(peer, current);

    private void OnFactsChanged() => GameThread.RunSafe(() =>
    {
        if (Volatile.Read(ref disposed) != 0 || !facts.TryCapture(out var snapshot)) return;
        network.SendAll(new NetworkRoomMissionFactsSnapshot(snapshot));
    }, context: nameof(ServerRoomMissionFactsHandler));

    private void Handle_Synchronized(MessagePayload<PlayerCampaignSynchronized> payload)
    {
        var peer = payload.What.PlayerId;
        GameThread.RunSafe(() =>
        {
            if (IsCurrent(peer)) TrySendInitial(peer, out _);
        }, context: nameof(ServerRoomMissionFactsHandler));
    }

    private void Handle_Applied(MessagePayload<NetworkRoomMissionFactsApplied> payload)
    {
        var peer = payload.Who as NetPeer;
        var version = payload.What.Version;
        if (!IsCurrent(peer)) return;
        GameThread.EnqueueSafe(() =>
        {
            if (!IsCurrent(peer) || !version.IsValid || !facts.HasApplied(version)) return;
            lock (applied) applied[peer] = version;
            Logger.Information("Confirmed room Mission membership applied by peer {Peer} revision {Revision} hash {Hash}",
                peer.Id, version.Revision, version.Sha256);
        }, context: nameof(ServerRoomMissionFactsHandler));
    }

    private void Handle_Disconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (payload.What.PlayerId == null) return;
        lock (applied) applied.Remove(payload.What.PlayerId);
    }
}
