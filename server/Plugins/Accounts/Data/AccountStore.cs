using System.Security.Cryptography;
using Cyberverse.Plugin.Accounts.Keycloak;
using Microsoft.EntityFrameworkCore;

namespace Cyberverse.Plugin.Accounts.Data;

/// <summary>The plugin's database work. Thread-safe: every call uses its own DbContext.</summary>
public sealed class AccountStore(DbContextOptions<AccountsDbContext> options, TimeSpan sessionLifetime, Func<DateTime> utcNow)
{
    private AccountsDbContext Open() => new(options);

    public async Task MigrateAsync(CancellationToken cancellation = default)
    {
        await using var db = Open();
        await db.Database.MigrateAsync(cancellation);
    }

    /// <summary>A new random session token (256 bits, base64url) for the client to keep.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>The unexpired session with this token, with its account.</summary>
    public async Task<Session?> FindSessionAsync(string token, CancellationToken cancellation = default)
    {
        var hash = Hash(token);
        var now = utcNow();
        await using var db = Open();
        return await db.Sessions.AsNoTracking().Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.ExpiresAt > now, cancellation);
    }

    /// <summary>Marks the session used now and moves its expiry.</summary>
    public async Task TouchSessionAsync(Guid sessionId, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        await db.Sessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(x => x
            .SetProperty(s => s.LastUsedAt, now)
            .SetProperty(s => s.ExpiresAt, now + sessionLifetime), cancellation);
        await db.Accounts.Where(x => x.Sessions.Any(s => s.Id == sessionId))
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastLoginAt, now), cancellation);
    }

    /// <summary>Creates the account for a Keycloak user on their first login, or updates it; the role was just checked.</summary>
    public async Task<Account> UpsertAccountAsync(UserTokens tokens, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.KeycloakSub == tokens.Sub, cancellation);
        if (account == null)
        {
            account = new Account { Id = Guid.NewGuid(), KeycloakSub = tokens.Sub, Name = tokens.Username, CreatedAt = now };
            db.Accounts.Add(account);
        }

        account.Name = tokens.Username;
        account.LastLoginAt = now;
        account.RefreshToken = tokens.RefreshToken;
        account.RoleCheckedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellation);
        }
        catch (DbUpdateException) when (db.Entry(account).State == EntityState.Added)
        {
            // The same user finished two logins at once; the other one created the account.
            return await UpsertAccountAsync(tokens, cancellation);
        }

        return account;
    }

    /// <summary>Records a role check: the rotated refresh token, or null when it stopped working.</summary>
    public async Task UpdateRoleCheckAsync(Guid accountId, string? refreshToken, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        await db.Accounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(x => x
            .SetProperty(a => a.RefreshToken, refreshToken)
            .SetProperty(a => a.RoleCheckedAt, now), cancellation);
    }

    /// <summary>Creates a session for a PC and returns its token, which isn't stored.</summary>
    public async Task<string> CreateSessionAsync(Guid accountId, string deviceName, CancellationToken cancellation = default)
    {
        var token = NewToken();
        var now = utcNow();
        await using var db = Open();
        db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TokenHash = Hash(token),
            DeviceName = deviceName.Length > 64 ? deviceName[..64] : deviceName,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now + sessionLifetime,
        });
        await db.SaveChangesAsync(cancellation);
        return token;
    }

    public async Task<int> DeleteSessionsAsync(Guid accountId, CancellationToken cancellation = default)
    {
        await using var db = Open();
        return await db.Sessions.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(cancellation);
    }

    /// <summary>Deletes one session; returns its account, or null when there was none.</summary>
    public async Task<Guid?> DeleteSessionAsync(Guid sessionId, CancellationToken cancellation = default)
    {
        await using var db = Open();
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId, cancellation);
        if (session == null)
        {
            return null;
        }

        db.Sessions.Remove(session);
        await db.SaveChangesAsync(cancellation);
        return session.AccountId;
    }

    public async Task<int> DeleteExpiredSessionsAsync(CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        return await db.Sessions.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(cancellation);
    }

    /// <summary>The ban in force for the account that ends last (a permanent one first), or null.</summary>
    public async Task<Ban?> ActiveBanAsync(Guid accountId, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        return await db.Bans.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now))
            .OrderBy(x => x.ExpiresAt == null ? 0 : 1).ThenByDescending(x => x.ExpiresAt)
            .FirstOrDefaultAsync(cancellation);
    }

    public async Task<Ban> AddBanAsync(Guid accountId, string reason, string createdBy, DateTime? expiresAt,
        CancellationToken cancellation = default)
    {
        await using var db = Open();
        var ban = new Ban
        {
            Id = Guid.NewGuid(), AccountId = accountId, Reason = reason, CreatedBy = createdBy, CreatedAt = utcNow(),
            ExpiresAt = expiresAt,
        };
        db.Bans.Add(ban);
        await db.SaveChangesAsync(cancellation);
        return ban;
    }

    /// <summary>Accounts with a refresh token whose role wasn't checked since before.</summary>
    public async Task<List<Account>> AccountsToRecheckAsync(DateTime before, CancellationToken cancellation = default)
    {
        await using var db = Open();
        return await db.Accounts.AsNoTracking()
            .Where(x => x.RefreshToken != null && x.RoleCheckedAt < before)
            .ToListAsync(cancellation);
    }

    /// <summary>An account by its name (case-insensitive), id or Keycloak user id.</summary>
    public async Task<Account?> FindAccountAsync(string nameOrId, CancellationToken cancellation = default)
    {
        await using var db = Open();
        if (Guid.TryParse(nameOrId, out var id) &&
            await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellation) is { } byId)
        {
            return byId;
        }

        var lower = nameOrId.ToLowerInvariant();
        return await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.KeycloakSub == nameOrId || x.Name.ToLower() == lower, cancellation);
    }

    public async Task<List<Session>> SessionsAsync(Guid accountId, CancellationToken cancellation = default)
    {
        await using var db = Open();
        return await db.Sessions.AsNoTracking().Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.LastUsedAt).ToListAsync(cancellation);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
