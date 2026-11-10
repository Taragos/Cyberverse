using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Networking;

public interface IConnections
{
    /// <summary>Sends a packet; its message id, channel and reliability come from the schema.</summary>
    bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>;

    /// <summary>
    /// Closes the connection once the reliable packets already sent to it have arrived, and removes its player.
    /// reason is an application end reason, 1000 or higher.
    /// </summary>
    bool Disconnect(uint connectionId, int reason, string debug);
}
