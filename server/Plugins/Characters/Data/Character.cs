namespace Cyberverse.Plugin.Characters.Data;

/// <summary>A character of an account. Deleting only hides it (<see cref="DeletedAt"/>); an admin can restore it.</summary>
public sealed class Character
{
    public Guid Id { get; set; }

    /// <summary>The player's account id from the login, or "dev:&lt;name&gt;" on a server without accounts.</summary>
    public required string AccountId { get; set; }

    public required string FirstName { get; set; }
    public required string LastName { get; set; }

    /// <summary>"first last" in lower case: unique among the characters that aren't deleted.</summary>
    public required string NameKey { get; set; }

    /// <summary>The character creator's options as the client encodes them. The server doesn't read it.</summary>
    public string Appearance { get; set; } = "";

    /// <summary>Where the character was last; null until it was played and saved once.</summary>
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? PositionZ { get; set; }
    public float Yaw { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public string Name => $"{FirstName} {LastName}";
}
