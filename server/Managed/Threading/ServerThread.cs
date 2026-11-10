using System.Collections.Concurrent;
using NLog;

namespace Cyberverse.Server.Threading;

/// <summary>The queue behind <see cref="IServerThread"/>; the main loop drains it with <see cref="RunPending"/>.</summary>
public sealed class ServerThread : IServerThread
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentQueue<Action> _actions = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Enqueue(action);
    }

    /// <summary>Runs the actions posted so far. One that throws is logged; the rest still run.</summary>
    public void RunPending()
    {
        // Only what was queued before this call, so an action that posts again can't keep the loop here.
        for (var count = _actions.Count; count > 0 && _actions.TryDequeue(out var action); count--)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Logger.Error(e, "An action posted to the server thread threw");
            }
        }
    }
}
