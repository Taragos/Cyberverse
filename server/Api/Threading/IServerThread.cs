namespace Cyberverse.Server.Threading;

/// <summary>
/// The server's state (players, entities, packets, events) belongs to the server thread. Code running elsewhere, e.g.
/// after an await on a database or HTTP call, posts its results back with <see cref="Post"/>.
/// </summary>
public interface IServerThread
{
    /// <summary>Runs action on the server thread at the start of its next update. Safe to call from any thread.</summary>
    void Post(Action action);
}
