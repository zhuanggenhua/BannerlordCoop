using LiteNetLib;

namespace Common.Network;

/// <summary>Authenticates the current client transport connection to its room host.</summary>
public interface IRoomHostPeerProvider
{
    /// <summary>Returns false after disconnect, replacement, or session disposal.</summary>
    bool IsCurrentRoomHostPeer(NetPeer peer);
}
