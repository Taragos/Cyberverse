using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Protocol;
using NLog;

namespace Cyberverse.Server.PacketHandling;

public enum DispatchResult
{
    Handled,
    NoHandler,
    Malformed,
}

public sealed class PacketDispatcher : IPacketDispatcher
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<ushort, IRoute> _routes = new();

    public IDisposable Register<T>(PacketHandler<T> handler) where T : struct, IServerBoundPacket<T>
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_routes.TryGetValue(T.MessageId, out var route))
        {
            route = new Route<T>();
            _routes.Add(T.MessageId, route);
        }

        if (route is not Route<T> typed)
        {
            throw new InvalidOperationException(
                $"Message id {T.MessageId} is taken by {route.PacketType.Name}, {typeof(T).Name} can't use it too");
        }

        typed.Handlers = [.. typed.Handlers, handler];
        return new Disposable(() => typed.Handlers = Arrays.WithoutFirst(typed.Handlers, handler));
    }

    /// <summary>Reads the packet once and runs every handler for its message id. Logs malformed packets and handler errors.</summary>
    public DispatchResult Dispatch(ushort messageId, uint connectionId, ReadOnlySpan<byte> body) =>
        _routes.TryGetValue(messageId, out var route) ? route.Dispatch(connectionId, body) : DispatchResult.NoHandler;

    private interface IRoute
    {
        Type PacketType { get; }

        DispatchResult Dispatch(uint connectionId, ReadOnlySpan<byte> body);
    }

    private sealed class Route<T> : IRoute where T : struct, IServerBoundPacket<T>
    {
        // Replaced on change, so a handler may (un)register while the packet runs.
        public PacketHandler<T>[] Handlers = [];

        public Type PacketType => typeof(T);

        public DispatchResult Dispatch(uint connectionId, ReadOnlySpan<byte> body)
        {
            var handlers = Handlers;
            if (handlers.Length == 0)
            {
                return DispatchResult.NoHandler;
            }

            if (!Packet.TryRead<T>(body, out var packet))
            {
                Logger.Warn($"Dropped a malformed {typeof(T).Name} ({body.Length} bytes) from connection {connectionId}");
                return DispatchResult.Malformed;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(connectionId, packet);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"{EventBus.Describe(handler)} threw on {typeof(T).Name} from connection {connectionId}");
                }
            }

            return DispatchResult.Handled;
        }
    }
}
