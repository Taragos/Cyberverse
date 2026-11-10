using Cyberverse.Server.Events;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.TweakDb.Generated;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server.PacketHandling;

/// <summary>Built-in plugin: players joining the world, moving, spawning cars and equipping items.</summary>
public class PlayerPacketHandler : IPlugin
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Random Random = new();
    private static readonly Character[] PossiblePlayerNpcChoice = [Character.Judy, Character.Panam, Character.Alt,
        Character.Claire, Character.Dexter, Character.Evelyn, Character.Hanako, Character.Jackie,
        Character.Mama_Welles, Character.Misty, Character.Stout, Character.Takemura, Character.Tbug,
        Character.wakako_okada, Character.Yorinobu];
    
    private EntityTracker? _tracker = null;
    private EntityService? _entities = null;
    private PlayerService? _players = null;
    private IEventBus? _events = null;

    public PluginInfo Info { get; } = new("player-sync", BuildInfo.ModVersion, "auth");

    protected void HandleJoinWorld(uint connectionId, PlayerJoinWorld content)
    {
        if (_players!.ConnectedPlayers.TryGetValue(connectionId, out var player))
        {
            Logger.Trace("Player {0} joined the world at ({1}, {2}, {3})", player.Name, 
                content.Position.X, content.Position.Y, content.Position.Z);

            var choice = Random.Next(PossiblePlayerNpcChoice.Length);
            var entity = _entities!.CreateEntity(PossiblePlayerNpcChoice[choice]);
            entity.WorldTransform = content.Position; // Spawn the entity at the right spot already
            entity.NetworkIdOwner = player.ConnectionId;
                
            // Since we only call the tracking for moved entities, force sync every entity to that player:
            _tracker!.InitialSpawnForPlayer(player);
            _events!.Publish(new PlayerJoinedWorld(player, content.Position));
        }
        else
        {
            Logger.Warn($"Could not find player with id {connectionId} in the player list.");
        }
    }

    protected void HandlePositionUpdate(uint connectionId, PlayerPositionUpdate content)
    {
        // // TODO: Implement differently, don't teleport.
        // TeleportEntity teleportEntity = default;
        // teleportEntity.networkedEntityId = 0;
        // teleportEntity.targetPosition = positionUpdate.worldTransform;
        // teleportEntity.yaw = positionUpdate.yaw;
        //                         
        // Message outputMessage = default;
        // outputMessage.MessageTypeClientBound(EMessageTypeClientbound.TeleportEntity);
        // outputMessage.channelId = 1;
        // outputMessage.connectionId = message.connectionId;
        // outputMessage.MarshalFrom(teleportEntity);
        //
        // EnqueueMessage(outputMessage);

        var playerEntities =
            _entities!.SpawnedEntities.Values.Where(tty => tty.NetworkIdOwner == connectionId);
        foreach (var entity in playerEntities)
        {
            //Console.WriteLine($"Move {player.Name} ({player.ConnectionId} / {message.connectionId}) to ({positionUpdate.WorldTransform.X}, {positionUpdate.WorldTransform.Y}, {positionUpdate.WorldTransform.Z})");
            
            // TODO: sometimes the game sends faulty updates to ~0, 3.16, 0.
            // TODO: This is happening before the savegame has loaded. A better solution is to prevent updating networkEntityIds that are not spawned yet,
            // because what happens here is that player.NetworkedEntityId is 0 because it has no entity yet, thus moving the old player.
            // Note: this should already be solved.
            if (content.WorldTransform.Z == 0 && content.WorldTransform.X == 0.010673523f)
            {
                // TODO: we should really remove that.
                Logger.Trace("Skipping position update");
                return;
            }
                    
            entity.WorldTransform = content.WorldTransform;
            entity.Yaw = content.Yaw;
            _tracker!.UpdateTrackingOf(entity);
        }
    }

    protected void HandleSpawnCar(uint connectionId, PlayerSpawnCar content)
    {
        var hash = RecordIdUtils.RecordIdToCrcHash(content.RecordId);
        var len = RecordIdUtils.RecordIdToNameLength(content.RecordId);
        
        Logger.Trace($"Player spawned a {hash} [{len}] at {content.WorldTransform}");
        
        DespawnAllVehiclesForPlayer(connectionId);

        var entity = _entities!.CreateEntity(content.RecordId);
        entity.WorldTransform = content.WorldTransform; // Spawn the entity at the right spot already
        entity.Yaw = content.Yaw;
        entity.NetworkIdOwner = connectionId;
        entity.IsVehicle = true;

        _tracker!.UpdateTrackingOf(entity);
    }

    private void DespawnAllVehiclesForPlayer(uint connectionId)
    {
        // Try to find existing cars for that owner.
        var existingVehicles = _entities!.SpawnedEntities
            .Select(x => x.Value)
            .Where(x => x.NetworkIdOwner == connectionId)
            .Where(x => x.IsVehicle)
            .ToList();

        foreach (var vehicle in existingVehicles)
        {
            vehicle.NetworkIdOwner = 0;
            _tracker!.StopTrackingOf(vehicle);
            _entities!.RemoveEntity(vehicle.NetworkedEntityId);
        }
    }

    protected void HandleUnmountCar(uint connectionId, PlayerUnmountCar content)
    {
        DespawnAllVehiclesForPlayer(connectionId);
    }

    private void HandleEquip(uint connectionId, PlayerEquipItem content)
    {
        Logger.Info($"Player did (un: {content.IsUnequipping})equip an item (slot {content.Slot}). Is it a weapon? {content.IsWeapon}");
        if (content.IsUnequipping)
        {
            // Try to find existing cars for that owner.
            var existingItems = _entities!.SpawnedEntities
                .Select(x => x.Value)
                .Where(x => x.NetworkIdOwner == connectionId)
                .Where(x => x.RecordId == content.ItemId)
                .ToList();

            // TODO: As long as we don't have NetworkedEntityIds, we need to unequip all of those.
            foreach (var item in existingItems)
            {
                item.NetworkIdOwner = 0;
                _tracker!.StopTrackingOf(item);
                _entities!.RemoveEntity(item.NetworkedEntityId);
            }
        }
        else
        {
            // TODO: How are we supposed to flag things as items? dedicated maps? But it'll still be entities in the end.
            // Also this is relevant when we have our own EquipItemEntity. Right now they are entities in the world.
            var playerEntity = _entities!.SpawnedEntities
                .Select(x => x.Value)
                .FirstOrDefault(x => x.NetworkIdOwner == connectionId);

            if (playerEntity == null)
            {
                return;
            }
            
            var entity = _entities!.CreateEntity(content.ItemId);
            entity.WorldTransform = playerEntity.WorldTransform; // TODO
            entity.Yaw = playerEntity.Yaw; // TODO
            entity.NetworkIdOwner = connectionId;
            entity.IsVehicle = false;

            _tracker!.UpdateTrackingOf(entity);
        }
    }

    private void HandleShoot(uint connectionId, PlayerShoot content)
    {
        Logger.Warn($"Shots fired! {content.ItemIdWeapon} at {content.StartPoint}");
    }

    public void OnLoad(IPluginContext context)
    {
        _tracker = context.Services.GetRequiredService<EntityTracker>();
        _entities = context.Services.GetRequiredService<EntityService>();
        _players = context.Services.GetRequiredService<PlayerService>();
        _events = context.Events;

        context.Packets.Register<PlayerJoinWorld>(HandleJoinWorld);
        context.Packets.Register<PlayerPositionUpdate>(HandlePositionUpdate);
        context.Packets.Register<PlayerSpawnCar>(HandleSpawnCar);
        context.Packets.Register<PlayerUnmountCar>(HandleUnmountCar);
        context.Packets.Register<PlayerEquipItem>(HandleEquip);
        context.Packets.Register<PlayerShoot>(HandleShoot);
    }
}
