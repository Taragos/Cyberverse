namespace Cyberverse.Server.Events;

/// <summary>Published once per server update, after the received packets were handled.</summary>
public sealed record ServerTick(DateTime UtcNow);

/// <summary>
/// A connection closed, whether it had logged in or not. For a player, <see cref="PlayerLeft"/> comes first.
/// </summary>
public sealed record ConnectionClosed(uint ConnectionId);
