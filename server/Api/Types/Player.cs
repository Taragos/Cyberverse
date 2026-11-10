namespace Cyberverse.Server.Types;

/// <summary>
/// Don't overuse this class, it's just a simple dataclass, we want to handle complex data in a different way
/// </summary>
public class Player
{
    /// <summary>The name shown to other players and in the logs.</summary>
    public string? Name;

    /// <summary>The account from the login provider, or null in development mode (no provider; nothing verified).</summary>
    public string? AccountId;

    public uint ConnectionId;
}
