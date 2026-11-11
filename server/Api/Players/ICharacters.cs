using Cyberverse.Server.Protocol;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Players;

/// <summary>The character a player plays as.</summary>
/// <param name="CharacterId">Stable id of the character.</param>
/// <param name="Name">Shown to other players and in the logs.</param>
/// <param name="Position">Where the character is; null keeps it where the player's game loaded (a new character).</param>
/// <param name="Appearance">The character creator's options as the client encodes them; may be empty.</param>
public sealed record CharacterSpawn(string CharacterId, string Name, Vector3? Position, float Yaw, string Appearance);

/// <summary>
/// Decides which character a player plays, e.g. the characters plugin with its select screen. Add one in
/// <see cref="Plugins.IPlugin.ConfigureServices"/>; there can be at most one. Without one, a player who joins the world
/// spawns right away where their game loaded (development mode).
/// </summary>
public interface ICharacterProvider
{
    /// <summary>
    /// The player's game loaded the world (PlayerJoinWorld at position). Called on the server thread, also again after a
    /// reload. The provider answers, now or later on the server thread, with <see cref="IWorldService.Spawn"/>.
    /// </summary>
    void PlayerJoinedGame(Player player, Vector3 position);
}

/// <summary>The players' character entities in the world.</summary>
public interface IWorldService
{
    /// <summary>
    /// Spawns the player's character entity and tells the client (CharacterSpawned), replacing the entity the player had
    /// before, so a player never has two. False when the player isn't connected or their game hasn't joined the world.
    /// </summary>
    bool Spawn(uint connectionId, CharacterSpawn character);

    /// <summary>The character the player plays, null before <see cref="Spawn"/>.</summary>
    CharacterSpawn? CharacterOf(uint connectionId);

    /// <summary>Where the player's character entity is now, as the client last reported it. False without one.</summary>
    bool TryGetTransform(uint connectionId, out Vector3 position, out float yaw);

    /// <summary>
    /// The player saved a new look for the character they play (e.g. at a mirror): everyone who sees the character
    /// gets it. False without a spawned character.
    /// </summary>
    bool ChangeLook(uint connectionId, string appearance);
}
