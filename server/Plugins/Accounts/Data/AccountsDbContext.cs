using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cyberverse.Plugin.Accounts.Data;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Ban> Bans => Set<Ban>();

    public static DbContextOptions<AccountsDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>(account =>
        {
            account.ToTable("accounts");
            account.HasIndex(x => x.KeycloakSub).IsUnique();
            account.Property(x => x.KeycloakSub).HasMaxLength(64);
            account.Property(x => x.Name).HasMaxLength(255);
        });

        model.Entity<Session>(session =>
        {
            session.ToTable("sessions");
            session.HasIndex(x => x.TokenHash).IsUnique();
            session.HasIndex(x => x.ExpiresAt);
            session.Property(x => x.TokenHash).HasMaxLength(32);
            session.Property(x => x.DeviceName).HasMaxLength(64);
            session.HasOne(x => x.Account).WithMany(x => x.Sessions).HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Ban>(ban =>
        {
            ban.ToTable("bans");
            ban.HasIndex(x => x.AccountId);
            ban.Property(x => x.Reason).HasMaxLength(1000);
            ban.Property(x => x.CreatedBy).HasMaxLength(255);
            ban.HasOne(x => x.Account).WithMany(x => x.Bans).HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>For `dotnet ef migrations add`: a connection string is needed to build the model, not a database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args) =>
        new(AccountsDbContext.Options("Host=localhost;Database=cyberverse"));
}
