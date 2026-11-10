using NLog;

namespace Cyberverse.Server.Events;

public sealed class EventBus : IEventBus
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Per event type an array that is replaced on change, so a subscriber may (un)subscribe while an event runs.
    private readonly Dictionary<Type, Delegate[]> _subscribers = new();

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subscribers[typeof(T)] = [.. Get(typeof(T)), handler];
        return new Disposable(() => _subscribers[typeof(T)] = Arrays.WithoutFirst(Get(typeof(T)), (Delegate)handler));
    }

    public void Publish<T>(T @event)
    {
        foreach (var subscriber in Get(typeof(T)))
        {
            try
            {
                ((Action<T>)subscriber)(@event);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"{Describe(subscriber)} threw on {typeof(T).Name}");
            }
        }
    }

    private Delegate[] Get(Type type) => _subscribers.GetValueOrDefault(type, []);

    internal static string Describe(Delegate handler) => $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
}
