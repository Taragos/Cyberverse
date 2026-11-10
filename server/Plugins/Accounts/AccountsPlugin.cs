using Cyberverse.Plugin.Accounts.Data;
using Cyberverse.Plugin.Accounts.Keycloak;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Threading;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Plugin.Accounts;

/// <summary>
/// Accounts in PostgreSQL, logins through Keycloak (CYB-00008). Registers the server's <see cref="ILoginProvider"/>: while
/// this plugin is installed, nobody gets in without an account, even when it failed to load.
/// </summary>
public sealed class AccountsPlugin : IPlugin
{
    /// <summary>The end reason for a player kicked because their sessions were revoked or they lost the role.</summary>
    public const int RevokedReason = 1003;

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    private readonly Provider _provider = new();
    private readonly CancellationTokenSource _stopping = new();
    private HttpClient? _http;
    private Task? _maintenance;

    public PluginInfo Info { get; } = new("accounts", "1.0.0", "auth");

    /// <summary>Set after OnLoad, for tests.</summary>
    internal LoginFlow? Flow => _provider.Flow;

    internal AccountStore? Store { get; private set; }

    /// <summary>Overrides the Keycloak client, for tests.</summary>
    internal Func<AccountsConfig, IKeycloak>? KeycloakFactory { get; init; }

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Stands in for the flow until OnLoad made it, and keeps logins shut if it never does.</summary>
    private sealed class Provider : ILoginProvider
    {
        public LoginFlow? Flow;
        public ILoginService? Logins;

        public void BeginLogin(LoginRequest request)
        {
            if (Flow == null)
            {
                Logins?.Reject(request.ConnectionId, "Logins are unavailable: the server's account system failed to start.");
                return;
            }

            Flow.BeginLogin(request);
        }

        public void CancelLogin(uint connectionId) => Flow?.CancelLogin(connectionId);
    }

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ILoginProvider>(_provider);

    public void OnLoad(IPluginContext context)
    {
        var logins = context.Services.GetRequiredService<ILoginService>();
        _provider.Logins = logins;
        var config = context.LoadConfig<AccountsConfig>().WithEnvironment(Environment.GetEnvironmentVariable);
        var store = new AccountStore(AccountsDbContext.Options(config.Database),
            TimeSpan.FromDays(config.SessionLifetimeDays), UtcNow);

        context.Logger.Info("Migrating the accounts database");
        store.MigrateAsync().GetAwaiter().GetResult();
        Store = store;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var keycloak = KeycloakFactory?.Invoke(config) ?? new KeycloakClient(config, _http);
        var thread = context.Services.GetRequiredService<IServerThread>();
        var connections = context.Services.GetRequiredService<IConnections>();
        var players = context.Services.GetRequiredService<IPlayerService>();
        var flow = new LoginFlow(store, keycloak, config, logins, connections, thread, context.Logger, UtcNow);
        _provider.Flow = flow;
        context.Logger.Info($"Players log in with Keycloak at {config.Issuer} and need the role {config.PlayerRole}");

        void Kick(Guid accountId)
        {
            var id = accountId.ToString();
            foreach (var player in players.Players.Where(x => x.AccountId == id).ToList())
            {
                connections.Disconnect(player.ConnectionId, RevokedReason, "Sessions revoked");
            }
        }

        _maintenance = Task.Run(() => MaintainAsync(store, flow, config, thread, context.Logger, Kick, _stopping.Token));
        RegisterCommands(context, store, thread, Kick);
    }

    public void OnUnload()
    {
        _stopping.Cancel();
        _provider.Flow?.CancelAll();
        _provider.Flow = null;
        try
        {
            _maintenance?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // cancelled
        }

        _http?.Dispose();
    }

    /// <summary>Hourly: deletes expired sessions and re-checks the role of accounts not checked for RoleCheckHours.</summary>
    private async Task MaintainAsync(AccountStore store, LoginFlow flow, AccountsConfig config, IServerThread thread,
        Logger logger, Action<Guid> kick, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceAsync(store, flow, config, thread, logger, kick, UtcNow(), stopping);
                await Task.Delay(MaintenanceInterval, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.Error(e, "Account maintenance failed");
                await Task.Delay(MaintenanceInterval, stopping).ContinueWith(_ => { });
            }
        }
    }

    internal static async Task RunMaintenanceAsync(AccountStore store, LoginFlow flow, AccountsConfig config,
        IServerThread thread, Logger logger, Action<Guid> kick, DateTime now, CancellationToken cancellation)
    {
        var expired = await store.DeleteExpiredSessionsAsync(cancellation);
        if (expired > 0)
        {
            logger.Info($"Deleted {expired} expired sessions");
        }

        var due = await store.AccountsToRecheckAsync(now - TimeSpan.FromHours(config.RoleCheckHours), cancellation);
        foreach (var account in due)
        {
            var result = await flow.CheckRoleAsync(account, cancellation);
            if (result is LoginFlow.RoleCheck.Lost or LoginFlow.RoleCheck.LoginAgain)
            {
                thread.Post(() => kick(account.Id));
            }
        }
    }

    private static void RegisterCommands(IPluginContext context, AccountStore store, IServerThread thread, Action<Guid> kick)
    {
        // The database calls run off the server thread; replies and kicks come back to it.
        void RunAsync(CommandContext ctx, Func<Action<string>, Task> work) => Task.Run(async () =>
        {
            var replies = new List<string>();
            try
            {
                await work(replies.Add);
            }
            catch (Exception e)
            {
                context.Logger.Error(e, $"Command {ctx.Name} failed");
                replies.Add($"Failed: {e.Message}");
            }

            thread.Post(() => replies.ForEach(ctx.Sender.Reply));
        });

        context.Commands.Register(new CommandDefinition("sessions", "Lists an account's logged-in PCs", ctx =>
        {
            if (ctx.Args.Count != 1)
            {
                ctx.Sender.Reply("Usage: sessions <account>");
                return;
            }

            RunAsync(ctx, async reply =>
            {
                if (await store.FindAccountAsync(ctx.Args[0]) is not { } account)
                {
                    reply($"No account {ctx.Args[0]}");
                    return;
                }

                var sessions = await store.SessionsAsync(account.Id);
                reply($"{account.Name} ({account.Id}): {sessions.Count} sessions, role checked {account.RoleCheckedAt:u}");
                foreach (var s in sessions)
                {
                    reply($"  {s.Id} {s.DeviceName}: last used {s.LastUsedAt:u}, expires {s.ExpiresAt:u}");
                }
            });
        }, Usage: "<account>", Permission: "accounts.sessions"));

        context.Commands.Register(new CommandDefinition("revoke-session", "Logs one PC out at its next join; it needs the browser login again", ctx =>
        {
            if (ctx.Args.Count != 1 || !Guid.TryParse(ctx.Args[0], out var sessionId))
            {
                ctx.Sender.Reply("Usage: revoke-session <session id from \"sessions\">");
                return;
            }

            RunAsync(ctx, async reply =>
            {
                var accountId = await store.DeleteSessionAsync(sessionId);
                reply(accountId == null ? $"No session {sessionId}" : $"Revoked session {sessionId}");
            });
        }, Usage: "<session id>", Permission: "accounts.revoke"));

        context.Commands.Register(new CommandDefinition("revoke-sessions", "Logs every PC of an account out and kicks it", ctx =>
        {
            if (ctx.Args.Count != 1)
            {
                ctx.Sender.Reply("Usage: revoke-sessions <account>");
                return;
            }

            RunAsync(ctx, async reply =>
            {
                if (await store.FindAccountAsync(ctx.Args[0]) is not { } account)
                {
                    reply($"No account {ctx.Args[0]}");
                    return;
                }

                var count = await store.DeleteSessionsAsync(account.Id);
                thread.Post(() => kick(account.Id));
                reply($"Revoked {count} sessions of {account.Name}");
            });
        }, Usage: "<account>", Permission: "accounts.revoke"));
    }
}
