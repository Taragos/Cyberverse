using Cyberverse.Server.Events;
using NLog;

namespace Cyberverse.Server.Services;

/// <summary>What happens when a connection closes, whether it logged in or not. Separate from GameServer for tests.</summary>
public sealed class ConnectionCleanup(PlayerService players, EntityService entities, EntityTracker tracker, IEventBus events)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Publishes <see cref="PlayerLeft"/> if the connection had a player, removes every entity it owns (despawning them
    /// for the players who saw them), removes the player, then publishes <see cref="ConnectionClosed"/>.
    /// </summary>
    public void CleanUp(uint connectionId)
    {
        if (players.ConnectedPlayers.TryGetValue(connectionId, out var player))
        {
            Logger.Info($"Player {player.Name} disconnected");
            events.Publish(new PlayerLeft(player));
        }

        // Also without a player: the login gate keeps such connections from spawning anything, but nothing may leak.
        var owned = entities.SpawnedEntities.Values.Where(x => x.NetworkIdOwner == connectionId).ToList();
        foreach (var entity in owned)
        {
            tracker.StopTrackingOf(entity);
            entities.RemoveEntity(entity.NetworkedEntityId);
        }

        players.ConnectedPlayers.Remove(connectionId);
        events.Publish(new ConnectionClosed(connectionId));
    }
}
