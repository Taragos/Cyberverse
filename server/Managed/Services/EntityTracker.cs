using Cyberverse.Server.Networking;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Services;

// TODO: This is just a hacked prototype, this can take any form of complexity and subtle bugs with it (and include dynamic visiblity)
public class EntityTracker
{
    public delegate bool EntityVisibilityFilter(Player player, Entity entity);

    private readonly IConnections _connections;
    private readonly PlayerService _players;
    private readonly EntityService _entities;
    private readonly Dictionary<uint, List<ulong>> _trackedEntities = new();
    private EntityVisibilityFilter? _visibilityFilter = null;

    public EntityTracker(IConnections connections, PlayerService players, EntityService entities)
    {
        _connections = connections;
        _players = players;
        _entities = entities;
    }

    public void SetEntityVisibilityFilter(EntityVisibilityFilter? filter)
    {
        _visibilityFilter = filter;
    }
    
    private void SendSpawnPacket(Entity entity, uint connectionId)
    {
        var spawnEntity = new SpawnEntity
        {
            NetworkedEntityId = entity.NetworkedEntityId,
            RecordId = entity.RecordId,
            SpawnPosition = entity.WorldTransform
        };
        _connections.Send(connectionId, spawnEntity);
    }

    private void SendDespawnPacket(ulong networkEntityId, uint connectionId)
    {
        var destroyEntity = new DestroyEntity { NetworkedEntityId = networkEntityId };
        _connections.Send(connectionId, destroyEntity);
    } 

    public void OnStartTrackingEntity(Player player, Entity entity)
    {
        if (!_trackedEntities.TryGetValue(player.ConnectionId, out var list))
        {
            _trackedEntities.Add(player.ConnectionId, [ entity.NetworkedEntityId ]);
            SendSpawnPacket(entity, player.ConnectionId);
            return;
        }
        
        if (list.Contains(entity.NetworkedEntityId))
        {
            // Already tracked!
            return;
        }
            
        list.Add(entity.NetworkedEntityId);
        SendSpawnPacket(entity, player.ConnectionId);
    }

    public void OnStopTrackingEntity(Player player, Entity entity)
    {
        if (!_trackedEntities.TryGetValue(player.ConnectionId, out var list))
        {
            // Not tracking anyone, so can't stop.
            return;
        }
        
        if (!list.Contains(entity.NetworkedEntityId))
        {
            // Not tracked.
            return;
        }

        list.Remove(entity.NetworkedEntityId);
        SendDespawnPacket(entity.NetworkedEntityId, player.ConnectionId);
    }

    public void UpdateTrackingOf(Entity entity)
    {
        foreach (var (connectionId, player) in _players.ConnectedPlayers)
        {
            // Don't track your own entities.
            if (entity.NetworkIdOwner == connectionId)
            {
                continue;
            }

            if (_visibilityFilter != null && !_visibilityFilter.Invoke(player, entity))
            {
                // In case the filter changed it's mind, remove the entity.
                OnStopTrackingEntity(player, entity);
                continue;
            }
            
            UpdateTrackingOf(entity, player);
        }
    }

    public void StopTrackingOf(Entity entity)
    {
        foreach (var (connectionId, player) in _players.ConnectedPlayers)
        {
            OnStopTrackingEntity(player, entity);
        }
    }

    public void UpdateTrackingOf(Entity entity, Player player)
    {
        // TODO: Challenge: Find the entity the player _currently_ wants to control. There could be multiple (e.g. in the car)
        // TODO: here, it's even more dumb, as we need to find out the camera at some point
        var playerEntities =
            _entities.SpawnedEntities.Values.Where(tty => tty.NetworkIdOwner == player.ConnectionId);
        foreach (var playerEntity in playerEntities)
        {
            // TODO: Check for out of bounds.
            var playerPosition = playerEntity.WorldTransform;
            if (playerPosition.DistanceSquared(entity.WorldTransform) <= 100 * 100)
            {
                // OnStartTrackingEntity just skips on already tracked entities, so we can avoid checking that condition
                // twice and just call it directly.
                OnStartTrackingEntity(player, entity);

                // TODO: In the future, we may not want to immediately update entities that have been recently spawned,
                //  because if spawning and updating are different channels, update may arrive before spawning.
                //  Though, the client should ignore updates for non existing/spawned entities.
                var teleportEntity = new TeleportEntity
                {
                    NetworkedEntityId = entity.NetworkedEntityId,
                    TargetPosition = entity.WorldTransform,
                    Yaw = entity.Yaw
                };

                //Console.WriteLine($"[{DateTime.Now.ToLongTimeString()}] Teleporting {entity.networkedEntityId} for {player.ConnectionId}");
                _connections.Send(player.ConnectionId, teleportEntity);
            }
            else
            {
                OnStopTrackingEntity(player, entity);
            }
        }
    }

    /// <summary>
    /// Since the entity tracker only works incremental on moved entities, a player spawning in wouldn't see resting
    /// entities. That's why we need to initiate tracking once (and in a batch!) for new players.
    /// </summary>
    /// <param name="player">The player to start tracking for</param>
    public void InitialSpawnForPlayer(Player player)
    {
        foreach (var (id, tty) in _entities.SpawnedEntities)
        {
            if (tty.NetworkIdOwner != player.ConnectionId)
            {
                //Console.WriteLine($"Force updating the tracking for the new player for {tty.networkedEntityId} == {key}");
                UpdateTrackingOf(tty, player);
            }
        }
    }
}