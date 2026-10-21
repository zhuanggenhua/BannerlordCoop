using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Serilog;
using LiteNetLib;
using System.Threading;

namespace Coop.Core.Client.Services.Instances;

public sealed class ClientRoomMissionFactsHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClientRoomMissionFactsHandler>();
    private readonly IMessageBroker broker;
    private readonly INetwork network;
    private readonly ISessionIdentity session;
    private readonly RoomMissionMembershipProjection projection;
    private int disposed;

    public ClientRoomMissionFactsHandler(IMessageBroker broker, INetwork network,
        ISessionIdentity session, RoomMissionMembershipProjection projection)
    {
        this.broker = broker;
        this.network = network;
        this.session = session;
        this.projection = projection;
        broker.Subscribe<NetworkRoomMissionFactsSnapshot>(Handle);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        broker.Unsubscribe<NetworkRoomMissionFactsSnapshot>(Handle);
    }

    private bool IsCurrent(NetPeer peer) => Volatile.Read(ref disposed) == 0 && peer != null &&
        network is IRoomHostPeerProvider provider && provider.IsCurrentRoomHostPeer(peer);

    private void Handle(MessagePayload<NetworkRoomMissionFactsSnapshot> payload)
    {
        var peer = payload.Who as NetPeer;
        if (!IsCurrent(peer)) return;
        var received = payload.What.Snapshot;
        // Detach before the queued apply; no native object resolution is needed for room metadata.
        var snapshot = received == null ? null : new RoomMissionFactsSnapshot(received.Version,
            received.MembershipComplete ? received.Members : null);
        GameThread.EnqueueSafe(() =>
        {
            if (!IsCurrent(peer) || snapshot == null || !snapshot.IsValid ||
                session.IsConfirmed && session.RoomId != snapshot.Version.RoomId) return;
            if (!projection.TryInstallComplete(snapshot))
            {
                Logger.Warning("Refused room Mission membership version {Revision} for room {RoomId}",
                    snapshot.Version.Revision, snapshot.Version.RoomId);
                return;
            }
            if (!IsCurrent(peer) || !projection.HasApplied(snapshot.Version)) return;
            network.SendAll(new NetworkRoomMissionFactsApplied(snapshot.Version));
            Logger.Information("Applied room Mission membership room {RoomId} revision {Revision} hash {Hash} members {Count}",
                snapshot.Version.RoomId, snapshot.Version.Revision, snapshot.Version.Sha256, snapshot.Members.Length);
        }, context: nameof(ClientRoomMissionFactsHandler));
    }
}
