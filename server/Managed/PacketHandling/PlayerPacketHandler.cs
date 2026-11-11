using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.TweakDb.Generated;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server.PacketHandling;

/// <summary>
/// Built-in plugin: players joining the world, moving, spawning cars and equipping items. A player's game joining the
/// world goes to the <see cref="ICharacterProvider"/> (the characters plugin), which picks the character and spawns it
/// through <see cref="IWorldService"/>. Without a provider the player spawns right away where their game loaded.
/// Characters spawn as the game's V without impostor, with their look sent along (EntityLook, CYB-00029).
/// </summary>
public class PlayerPacketHandler : IPlugin, IWorldService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private EntityTracker? _tracker = null;
    private EntityService? _entities = null;
    private PlayerService? _players = null;
    private IConnections? _connections = null;
    private IEventBus? _events = null;
    private ICharacterProvider? _provider = null;
    private readonly Dictionary<uint, InWorld> _inWorld = new();

    /// <summary>A player whose game joined the world.</summary>
    private sealed class InWorld
    {
        /// <summary>Where the game loaded, from the last PlayerJoinWorld.</summary>
        public Vector3 GamePosition;

        public ulong? EntityId;
        public CharacterSpawn? Character;

        /// <summary>The player was sent every entity near them since their game last loaded.</summary>
        public bool Synced;
    }

    public PluginInfo Info { get; } = new("player-sync", BuildInfo.ModVersion, "auth");

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IWorldService>(this);

    protected void HandleJoinWorld(uint connectionId, PlayerJoinWorld content)
    {
        if (!_players!.ConnectedPlayers.TryGetValue(connectionId, out var player))
        {
            Logger.Warn($"Could not find player with id {connectionId} in the player list.");
            return;
        }

        Logger.Trace("Player {0} joined the world at ({1}, {2}, {3})", player.Name,
            content.Position.X, content.Position.Y, content.Position.Z);

        if (!_inWorld.TryGetValue(connectionId, out var state))
        {
            state = new InWorld();
            _inWorld.Add(connectionId, state);
        }
        else
        {
            // A reload (e.g. after dying): the game dropped every puppet it had spawned for us, so send them again.
            Logger.Debug($"{player.Name} joined the world again (connection {connectionId})");
            _tracker!.ResetFor(player);
            state.Synced = false;
        }

        state.GamePosition = content.Position;

        if (_provider == null)
        {
            Spawn(connectionId, state.Character ?? new CharacterSpawn("dev:" + player.Name, player.Name ?? "", null, 0, ""));
            return;
        }

        try
        {
            _provider.PlayerJoinedGame(player, content.Position);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"The character provider threw for {player.Name} (connection {connectionId})");
        }
    }

    public bool Spawn(uint connectionId, CharacterSpawn character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!_players!.ConnectedPlayers.TryGetValue(connectionId, out var player) ||
            !_inWorld.TryGetValue(connectionId, out var state))
        {
            return false;
        }

        // One character entity per player: a respawn or a second join replaces it.
        if (state.EntityId is { } oldId && _entities!.SpawnedEntities.TryGetValue(oldId, out var old))
        {
            _tracker!.StopTrackingOf(old);
            _entities.RemoveEntity(oldId);
        }

        var position = character.Position ?? state.GamePosition;
        // The game's own cutscene V without impostor, which copies nothing from the local player; each client puts the
        // character's look on it (EntityLook, CYB-00029).
        var entity = _entities!.CreateEntity(RecordFor(character.Appearance));
        entity.Look = character.Appearance;
        entity.WorldTransform = position; // Spawn the entity at the right spot already
        entity.Yaw = character.Yaw;
        entity.NetworkIdOwner = connectionId;
        state.EntityId = entity.NetworkedEntityId;
        state.Character = character;

        _connections!.Send(connectionId, new CharacterSpawned
        {
            CharacterId = character.CharacterId,
            Name = character.Name,
            Teleport = character.Position != null,
            Position = position,
            Yaw = character.Yaw,
            Appearance = character.Appearance,
        });

        if (!state.Synced)
        {
            // Since we only call the tracking for moved entities, force sync every entity to that player:
            _tracker!.InitialSpawnForPlayer(player);
            state.Synced = true;
        }

        _tracker!.UpdateTrackingOf(entity);
        Logger.Info($"{player.Name} plays {character.Name} (connection {connectionId})");
        _events!.Publish(new PlayerJoinedWorld(player, position));
        return true;
    }

    /// <summary>The V record for a stored look: female when it says body=f, else male.</summary>
    public static Character RecordFor(string appearance) =>
        appearance.Split(' ').Contains("body=f")
            ? Character.TPP_Player_Cutscene_No_Impostor_Female
            : Character.TPP_Player_Cutscene_No_Impostor_Male;

    public bool ChangeLook(uint connectionId, string appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!_inWorld.TryGetValue(connectionId, out var state) || state.Character is not { } character ||
            state.EntityId is not { } id || !_entities!.SpawnedEntities.TryGetValue(id, out var entity))
        {
            return false;
        }

        // The body can't change at a mirror (CYB-00027), so the V record stays; only the look is sent again.
        state.Character = character with { Appearance = appearance };
        entity.Look = appearance;
        _tracker!.UpdateLook(entity);
        return true;
    }

    public CharacterSpawn? CharacterOf(uint connectionId) =>
        _inWorld.TryGetValue(connectionId, out var state) ? state.Character : null;

    public bool TryGetTransform(uint connectionId, out Vector3 position, out float yaw)
    {
        if (_inWorld.TryGetValue(connectionId, out var state) && state.EntityId is { } id &&
            _entities!.SpawnedEntities.TryGetValue(id, out var entity))
        {
            position = entity.WorldTransform;
            yaw = entity.Yaw;
            return true;
        }

        position = default;
        yaw = 0;
        return false;
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
        _connections = context.Services.GetRequiredService<IConnections>();
        _events = context.Events;
        _provider = context.Services.GetService<ICharacterProvider>();
        if (_provider == null)
        {
            Logger.Warn("No character provider (characters plugin): players spawn where their game loaded");
        }

        // PlayerLeft comes before the cleanup, so the providers can still read the transform.
        context.Events.Subscribe<ConnectionClosed>(e => _inWorld.Remove(e.ConnectionId));

        context.Packets.Register<PlayerJoinWorld>(HandleJoinWorld);
        context.Packets.Register<PlayerPositionUpdate>(HandlePositionUpdate);
        context.Packets.Register<PlayerSpawnCar>(HandleSpawnCar);
        context.Packets.Register<PlayerUnmountCar>(HandleUnmountCar);
        context.Packets.Register<PlayerEquipItem>(HandleEquip);
        context.Packets.Register<PlayerShoot>(HandleShoot);
    }
}
