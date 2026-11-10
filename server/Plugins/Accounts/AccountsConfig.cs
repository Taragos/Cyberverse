namespace Cyberverse.Plugin.Accounts;

/// <summary>
/// config/accounts.json, written with these defaults on the first start (they match the Docker Compose setup).
/// Environment variables override the secrets and the public URL, which is easier with Docker:
/// CYBERVERSE_ACCOUNTS_DATABASE, CYBERVERSE_ACCOUNTS_CLIENT_SECRET and CYBERVERSE_ACCOUNTS_ISSUER.
/// </summary>
public sealed class AccountsConfig
{
    /// <summary>Npgsql connection string of the game database.</summary>
    public string Database { get; set; } = "Host=postgres;Database=cyberverse;Username=cyberverse;Password=cyberverse;GSS Encryption Mode=Disable";

    /// <summary>
    /// The realm's public URL: the issuer in the tokens, and what players' browsers open. Behind a reverse proxy this is
    /// the https address.
    /// </summary>
    public string Issuer { get; set; } = "http://localhost:8080/realms/cyberverse";

    /// <summary>Where the server itself reaches the realm, if not at <see cref="Issuer"/> (e.g. inside Docker). Null: Issuer.</summary>
    public string? InternalUrl { get; set; } = "http://keycloak:8080/realms/cyberverse";

    public string ClientId { get; set; } = "cyberverse-server";

    public string ClientSecret { get; set; } = "change-me";

    /// <summary>The realm role that whitelists a user.</summary>
    public string PlayerRole { get; set; } = "cyberverse-player";

    /// <summary>A session expires after this many days without a login.</summary>
    public int SessionLifetimeDays { get; set; } = 30;

    /// <summary>The whitelist role is checked with Keycloak on every join and at least this often.</summary>
    public int RoleCheckHours { get; set; } = 24;

    /// <summary>Allows plain http to Keycloak. Only for local setups.</summary>
    public bool AllowHttp { get; set; } = true;

    public string BaseUrl => (InternalUrl ?? Issuer).TrimEnd('/');

    public AccountsConfig WithEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        if (getEnvironmentVariable("CYBERVERSE_ACCOUNTS_DATABASE") is { Length: > 0 } database)
        {
            Database = database;
        }

        if (getEnvironmentVariable("CYBERVERSE_ACCOUNTS_CLIENT_SECRET") is { Length: > 0 } secret)
        {
            ClientSecret = secret;
        }

        if (getEnvironmentVariable("CYBERVERSE_ACCOUNTS_ISSUER") is { Length: > 0 } issuer)
        {
            Issuer = issuer;
        }

        return this;
    }
}
