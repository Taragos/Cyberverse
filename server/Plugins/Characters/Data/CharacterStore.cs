using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cyberverse.Plugin.Characters.Data;

public enum CreateResult
{
    Created,
    LimitReached,
    NameTaken,
}

public enum RestoreResult
{
    Restored,
    NotFound,
    NotDeleted,
    NameTaken,
}

/// <summary>The plugin's database work. Thread-safe: every call uses its own DbContext.</summary>
public sealed class CharacterStore(DbContextOptions<CharactersDbContext> options, Func<DateTime> utcNow)
{
    private CharactersDbContext Open() => new(options);

    public async Task MigrateAsync(CancellationToken cancellation = default)
    {
        await using var db = Open();
        await db.Database.MigrateAsync(cancellation);
    }

    /// <summary>The account's characters that aren't deleted, last played first.</summary>
    public async Task<List<Character>> ListAsync(string accountId, CancellationToken cancellation = default)
    {
        await using var db = Open();
        return await db.Characters.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.DeletedAt == null)
            .OrderByDescending(x => x.LastPlayedAt ?? x.CreatedAt)
            .ToListAsync(cancellation);
    }

    /// <summary>Creates a character with already normalized names, unless the account has max characters or the name is taken.</summary>
    public async Task<(CreateResult Result, Character? Character)> CreateAsync(string accountId, string firstName,
        string lastName, string appearance, int max, CancellationToken cancellation = default)
    {
        await using var db = Open();
        var count = await db.Characters.CountAsync(x => x.AccountId == accountId && x.DeletedAt == null, cancellation);
        if (count >= max)
        {
            return (CreateResult.LimitReached, null);
        }

        var character = new Character
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FirstName = firstName,
            LastName = lastName,
            NameKey = CharacterNames.Key(firstName, lastName),
            Appearance = appearance,
            CreatedAt = utcNow(),
        };
        db.Characters.Add(character);
        try
        {
            await db.SaveChangesAsync(cancellation);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return (CreateResult.NameTaken, null);
        }

        return (CreateResult.Created, character);
    }

    /// <summary>The account's character with this id, unless it is deleted.</summary>
    public async Task<Character?> FindAsync(string accountId, Guid id, CancellationToken cancellation = default)
    {
        await using var db = Open();
        return await db.Characters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.AccountId == accountId && x.DeletedAt == null, cancellation);
    }

    public async Task MarkPlayedAsync(Guid id, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        await db.Characters.Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastPlayedAt, now), cancellation);
    }

    public async Task SavePositionsAsync(IReadOnlyCollection<(Guid Id, float X, float Y, float Z, float Yaw)> positions,
        CancellationToken cancellation = default)
    {
        await using var db = Open();
        foreach (var (id, x, y, z, yaw) in positions)
        {
            await db.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(c => c
                .SetProperty(p => p.PositionX, x)
                .SetProperty(p => p.PositionY, y)
                .SetProperty(p => p.PositionZ, z)
                .SetProperty(p => p.Yaw, yaw), cancellation);
        }
    }

    public async Task SaveAppearanceAsync(Guid id, string appearance, CancellationToken cancellation = default)
    {
        await using var db = Open();
        await db.Characters.Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.Appearance, appearance), cancellation);
    }

    /// <summary>Hides the account's character and frees its name. False when there is no such character.</summary>
    public async Task<bool> DeleteAsync(string accountId, Guid id, CancellationToken cancellation = default)
    {
        var now = utcNow();
        await using var db = Open();
        return await db.Characters.Where(x => x.Id == id && x.AccountId == accountId && x.DeletedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, now), cancellation) > 0;
    }

    /// <summary>For admins: the characters of an account id, or the one with this name, deleted ones included.</summary>
    public async Task<List<Character>> SearchAsync(string accountIdOrName, CancellationToken cancellation = default)
    {
        var key = accountIdOrName.Trim().ToLowerInvariant();
        await using var db = Open();
        return await db.Characters.AsNoTracking()
            .Where(x => x.AccountId == accountIdOrName || x.NameKey == key)
            .OrderBy(x => x.AccountId).ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellation);
    }

    /// <summary>Brings a deleted character back, optionally under new (normalized) names when the old one was taken.</summary>
    public async Task<RestoreResult> RestoreAsync(Guid id, string? firstName, string? lastName,
        CancellationToken cancellation = default)
    {
        await using var db = Open();
        var character = await db.Characters.FirstOrDefaultAsync(x => x.Id == id, cancellation);
        if (character == null)
        {
            return RestoreResult.NotFound;
        }

        if (character.DeletedAt == null)
        {
            return RestoreResult.NotDeleted;
        }

        if (firstName != null && lastName != null)
        {
            character.FirstName = firstName;
            character.LastName = lastName;
            character.NameKey = CharacterNames.Key(firstName, lastName);
        }

        character.DeletedAt = null;
        try
        {
            await db.SaveChangesAsync(cancellation);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return RestoreResult.NameTaken;
        }

        return RestoreResult.Restored;
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
