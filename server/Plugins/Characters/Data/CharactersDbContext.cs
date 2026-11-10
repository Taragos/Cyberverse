using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cyberverse.Plugin.Characters.Data;

public sealed class CharactersDbContext(DbContextOptions<CharactersDbContext> options) : DbContext(options)
{
    public DbSet<Character> Characters => Set<Character>();

    /// <summary>Same database as the accounts plugin, with its own migrations history.</summary>
    public static DbContextOptions<CharactersDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<CharactersDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history_characters"))
            .UseSnakeCaseNamingConvention()
            .Options;

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Character>(character =>
        {
            character.ToTable("characters");
            character.HasIndex(x => x.AccountId);
            character.HasIndex(x => x.NameKey).IsUnique().HasFilter("deleted_at IS NULL");
            character.Property(x => x.AccountId).HasMaxLength(64);
            character.Property(x => x.FirstName).HasMaxLength(64);
            character.Property(x => x.LastName).HasMaxLength(64);
            character.Property(x => x.NameKey).HasMaxLength(160);
            character.Ignore(x => x.Name);
        });
    }
}

/// <summary>For `dotnet ef migrations add`: a connection string is needed to build the model, not a database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CharactersDbContext>
{
    public CharactersDbContext CreateDbContext(string[] args) =>
        new(CharactersDbContext.Options("Host=localhost;Database=cyberverse"));
}
