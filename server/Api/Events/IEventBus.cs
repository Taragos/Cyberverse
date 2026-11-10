namespace Cyberverse.Server.Events;

/// <summary>
/// Synchronous events on the server thread. Subscribers run in the order they subscribed (i.e. plugin load order); one
/// that throws is logged and the others still run. Any type can be an event, so plugins can publish their own.
/// </summary>
public interface IEventBus
{
    /// <summary>Calls handler for every published event of exactly type T. Dispose the result to unsubscribe.</summary>
    IDisposable Subscribe<T>(Action<T> handler);

    void Publish<T>(T @event);
}
