namespace Cyberverse.Plugin.Accounts.Data;

/// <summary>A player's account: one per Keycloak user, whatever PC they log in from.</summary>
public sealed class Account
{
    public Guid Id { get; set; }

    /// <summary>The Keycloak user id (the tokens' sub claim). Never changes for a user.</summary>
    public required string KeycloakSub { get; set; }

    /// <summary>The Keycloak username at the last login (preferred_username).</summary>
    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }

    /// <summary>
    /// Keycloak offline refresh token from the last browser login, for checking the whitelist role without the player.
    /// Null after it stopped working.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>When the whitelist role was last confirmed with Keycloak.</summary>
    public DateTime RoleCheckedAt { get; set; }

    public List<Session> Sessions { get; set; } = [];
    public List<Ban> Bans { get; set; } = [];
}

/// <summary>A logged-in PC. The client keeps the token; only its SHA-256 is stored.</summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public required byte[] TokenHash { get; set; }

    /// <summary>The PC's name as the client reported it. Not verified.</summary>
    public required string DeviceName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }

    /// <summary>LastUsedAt plus the session lifetime; moved on every use.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Keeps an account out of the server, for a while or for good. Managed by the admin tools (CYB-00017).</summary>
public sealed class Ban
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>Shown to the player.</summary>
    public required string Reason { get; set; }

    /// <summary>Who banned: an admin's account name or "console".</summary>
    public required string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null for a permanent ban.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set when an admin lifts the ban early; the row stays for the history.</summary>
    public DateTime? RevokedAt { get; set; }
}
