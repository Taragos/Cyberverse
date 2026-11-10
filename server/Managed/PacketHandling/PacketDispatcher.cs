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

    /// <summary>The connection hasn't logged in and the packet isn't allowed before that.</summary>
    NotLoggedIn,
}

/// <param name="isLoggedIn">
/// Whether a connection has a player. Packets from connections without one are dropped unless they were allowed with
/// <see cref="AllowBeforeLogin{T}"/>. Null lets every packet through (for tests).
/// </param>
public sealed class PacketDispatcher(Func<uint, bool>? isLoggedIn = null) : IPacketDispatcher
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<ushort, IRoute> _routes = new();

    // Message id -> how many registrations allow it before the login.
    private readonly Dictionary<ushort, int> _beforeLogin = new();

    public IDisposable AllowBeforeLogin<T>() where T : struct, IServerBoundPacket<T>
    {
        _beforeLogin[T.MessageId] = _beforeLogin.GetValueOrDefault(T.MessageId) + 1;
        return new Disposable(() =>
        {
            if (--_beforeLogin[T.MessageId] == 0)
            {
                _beforeLogin.Remove(T.MessageId);
            }
        });
    }

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
    public DispatchResult Dispatch(ushort messageId, uint connectionId, ReadOnlySpan<byte> body)
    {
        if (isLoggedIn != null && !_beforeLogin.ContainsKey(messageId) && !isLoggedIn(connectionId))
        {
            return DispatchResult.NotLoggedIn;
        }

        return _routes.TryGetValue(messageId, out var route) ? route.Dispatch(connectionId, body) : DispatchResult.NoHandler;
    }

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
