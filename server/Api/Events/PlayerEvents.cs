using Cyberverse.Server.Protocol;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Events;

/// <summary>A client logged in (versions accepted); the player is in <see cref="Players.IPlayerService"/> now.</summary>
public sealed record PlayerAuthenticated(Player Player);

/// <summary>An authenticated player's game loaded and their character spawned at Position.</summary>
public sealed record PlayerJoinedWorld(Player Player, Vector3 Position);

/// <summary>
/// A player's connection closed. Published before the server removes the player and their entities, so subscribers can
/// still read their state.
/// </summary>
public sealed record PlayerLeft(Player Player);
