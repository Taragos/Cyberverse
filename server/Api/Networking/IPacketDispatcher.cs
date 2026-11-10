using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Networking;

/// <summary>Handles a packet a client sent; connectionId identifies the client.</summary>
public delegate void PacketHandler<in T>(uint connectionId, T packet) where T : struct;

/// <summary>
/// Routes received packets to handlers. A message type can have several handlers: the packet is read once, and every
/// handler runs in registration order. A handler that throws is logged; the others still run.
/// </summary>
public interface IPacketDispatcher
{
    /// <summary>
    /// Calls handler for every packet of type T. A packet that fails to read is logged and dropped before any handler.
    /// Dispose the result to remove the handler. Throws when another packet type already uses T's message id.
    /// </summary>
    IDisposable Register<T>(PacketHandler<T> handler) where T : struct, IServerBoundPacket<T>;
}
