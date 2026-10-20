using Common;
using Common.Logging;
using Common.Network;
using Common.PacketHandlers;
using Common.Serialization;
using GameInterface.Services.Missions;
using LiteNetLib;
using Missions.Agents.Packets;
using Serilog;
using System;
using System.Threading;

namespace Missions.Services.Network;

public sealed class LocalMissionRelayReceiver : ILocalMissionRelayReceiver, IMissionPacketSourcePolicy, IDisposable
{
    private static readonly ILogger Logger = LogManager.GetLogger<LocalMissionRelayReceiver>();
    [ThreadStatic] private static Dispatch currentDispatch;
    private readonly IMissionRelaySourceVerifier verifier;
    private readonly IMissionContext context;
    private readonly ICommonSerializer serializer;
    private readonly IPacketManager packets;
    private readonly IMovementPacketCompressor movementCompressor;
    private int disposed;

    private sealed class Dispatch
    {
        internal LocalMissionRelayReceiver Owner;
        internal MissionRelayReceipt Relay;
        internal long SceneGeneration;
    }

    private sealed class SourceReceipt : MissionPacketSourceReceipt
    {
        internal readonly Dispatch Dispatch;
        internal SourceReceipt(Dispatch dispatch) : base(dispatch.Relay.Peer, dispatch.Relay.ControllerId)
        { Dispatch = dispatch; }
    }

    public LocalMissionRelayReceiver(IMissionRelaySourceVerifier verifier, IMissionContext context,
        ICommonSerializer serializer, IPacketManager packets, IMovementPacketCompressor movementCompressor)
    {
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.packets = packets ?? throw new ArgumentNullException(nameof(packets));
        this.movementCompressor = movementCompressor ?? throw new ArgumentNullException(nameof(movementCompressor));
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    public bool TryReceive(MissionRelayReceipt receipt, byte[] payload)
    {
        if (receipt == null || payload == null || Volatile.Read(ref disposed) != 0 || !verifier.IsCurrent(receipt) ||
            !context.TryGetInstanceGeneration(receipt.InstanceId, out var generation)) return false;
        object decoded;
        try
        {
            decoded = serializer.Deserialize(payload);
            if (decoded is CompressedMovementPacket compressed)
            {
                if (!movementCompressor.TryRestore(compressed, out var restored)) return false;
                decoded = restored;
            }
        }
        catch (Exception error)
        {
            Logger.Error(error, "Could not decode verified local Mission relay from {Controller}", receipt.ControllerId);
            return false;
        }
        string sender = decoded is MovementPacket movement ? movement.SenderControllerId
            : decoded is MountMovementPacket mounts ? mounts.SenderControllerId : null;
        if (decoded is not MovementPacket && decoded is not MountMovementPacket)
        {
            Logger.Warning("Local Mission relay type {Type} is not implemented; reliable spawn/action delivery remains gated", decoded?.GetType());
            return false;
        }
        if (sender != receipt.ControllerId) return false;
        var dispatch = new Dispatch { Owner = this, Relay = receipt, SceneGeneration = generation };
        GameThread.EnqueueSafe(() =>
        {
            if (!IsCurrent(dispatch)) return;
            var previous = currentDispatch;
            currentDispatch = dispatch;
            try { packets.HandleReceive(receipt.Peer, (IPacket)decoded); }
            finally { currentDispatch = previous; }
        }, context: nameof(LocalMissionRelayReceiver));
        return true;
    }

    public bool TryCapture(NetPeer peer, string senderControllerId, out MissionPacketSourceReceipt receipt)
    {
        receipt = null;
        var dispatch = currentDispatch;
        if (dispatch == null || !ReferenceEquals(dispatch.Owner, this) ||
            !ReferenceEquals(peer, dispatch.Relay.Peer) || senderControllerId != dispatch.Relay.ControllerId ||
            !IsCurrent(dispatch)) return false;
        receipt = new SourceReceipt(dispatch);
        return true;
    }

    public bool IsCurrent(MissionPacketSourceReceipt receipt) => receipt is SourceReceipt source &&
        ReferenceEquals(source.Dispatch.Owner, this) && IsCurrent(source.Dispatch);

    private bool IsCurrent(Dispatch dispatch) => Volatile.Read(ref disposed) == 0 &&
        verifier.IsCurrent(dispatch.Relay) && context.TryGetInstanceGeneration(dispatch.Relay.InstanceId, out var generation) &&
        generation == dispatch.SceneGeneration;
}
