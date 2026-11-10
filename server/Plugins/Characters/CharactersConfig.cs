namespace Cyberverse.Plugin.Characters;

/// <summary>
/// config/characters.json, written with these defaults on the first start (they match the Docker Compose setup).
/// CYBERVERSE_CHARACTERS_DATABASE overrides the connection string, else CYBERVERSE_ACCOUNTS_DATABASE: both plugins
/// normally share one database.
/// </summary>
public sealed class CharactersConfig
{
    /// <summary>Npgsql connection string of the game database.</summary>
    public string Database { get; set; } = "Host=postgres;Database=cyberverse;Username=cyberverse;Password=cyberverse;GSS Encryption Mode=Disable";

    /// <summary>How many characters an account may have (deleted ones don't count).</summary>
    public int MaxCharacters { get; set; } = 3;

    /// <summary>The positions of the characters being played are saved this often, and when the player leaves.</summary>
    public int SaveIntervalSeconds { get; set; } = 60;

    public CharactersConfig WithEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        if ((getEnvironmentVariable("CYBERVERSE_CHARACTERS_DATABASE") ?? getEnvironmentVariable("CYBERVERSE_ACCOUNTS_DATABASE"))
            is { Length: > 0 } database)
        {
            Database = database;
        }

        return this;
    }
}
