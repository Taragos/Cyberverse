using Cyberverse.Plugin.Accounts.Data;
using Cyberverse.Plugin.Accounts.Keycloak;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Threading;
using NLog;

namespace Cyberverse.Plugin.Accounts;

/// <summary>
/// Logs players in: with a saved session token, or else through Keycloak's device login in the browser. The work runs
/// off the server thread; packets and the decision go back through <see cref="IServerThread"/>.
/// </summary>
public sealed class LoginFlow(
    AccountStore store,
    IKeycloak keycloak,
    AccountsConfig config,
    ILoginService logins,
    IConnections connections,
    IServerThread thread,
    Logger logger,
    Func<DateTime> utcNow,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : ILoginProvider
{
    public const string NotWhitelisted = "Your account is not whitelisted on this server yet. Ask an admin to add you.";
    public const string LoginFailed = "The server could not check your login. Try again later.";
    public const string BrowserLoginDenied = "The login in the browser was cancelled or the code expired. Connect again to retry.";

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    // Server thread only.
    private readonly Dictionary<uint, CancellationTokenSource> _running = new();

    /// <summary>Finishes when every login in progress has; for tests and shutdown.</summary>
    private readonly List<Task> _tasks = [];

    private TimeSpan RoleCheckInterval => TimeSpan.FromHours(config.RoleCheckHours);

    public void BeginLogin(LoginRequest request)
    {
        CancelLogin(request.ConnectionId);
        var cancellation = new CancellationTokenSource();
        _running[request.ConnectionId] = cancellation;
        var task = Task.Run(() => RunAsync(request, cancellation));
        lock (_tasks)
        {
            _tasks.RemoveAll(x => x.IsCompleted);
            _tasks.Add(task);
        }
    }

    public void CancelLogin(uint connectionId)
    {
        if (_running.Remove(connectionId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    /// <summary>Cancels every login in progress, e.g. when the plugin unloads.</summary>
    public void CancelAll()
    {
        foreach (var connectionId in _running.Keys.ToList())
        {
            CancelLogin(connectionId);
        }
    }

    public Task WhenIdle()
    {
        lock (_tasks)
        {
            return Task.WhenAll(_tasks.ToList());
        }
    }

    private async Task RunAsync(LoginRequest request, CancellationTokenSource cancellation)
    {
        Outcome outcome;
        try
        {
            outcome = await LoginAsync(request, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            logger.Error(e, $"Login of connection {request.ConnectionId} failed");
            outcome = Outcome.Reject(LoginFailed);
        }

        thread.Post(() =>
        {
            // Only the login this task belongs to; a newer one may have replaced it.
            if (!_running.TryGetValue(request.ConnectionId, out var current) || current != cancellation)
            {
                return;
            }

            _running.Remove(request.ConnectionId);
            if (outcome.Identity != null)
            {
                logins.Accept(request.ConnectionId, outcome.Identity);
            }
            else
            {
                logins.Reject(request.ConnectionId, outcome.Message!);
            }
        });
    }

    private sealed record Outcome(LoginIdentity? Identity, string? Message)
    {
        public static Outcome Accept(Account account) => new(new LoginIdentity(account.Id.ToString(), account.Name), null);

        public static Outcome Reject(string message) => new(null, message);
    }

    private async Task<Outcome> LoginAsync(LoginRequest request, CancellationToken cancellation)
    {
        if (request.SessionToken.Length > 0)
        {
            var session = await store.FindSessionAsync(request.SessionToken, cancellation);
            if (session?.Account is { } account)
            {
                var role = await CheckRoleAsync(account, cancellation);
                if (role == RoleCheck.Lost)
                {
                    return Outcome.Reject(NotWhitelisted);
                }

                if (role == RoleCheck.Unavailable)
                {
                    return Outcome.Reject(LoginFailed);
                }

                if (role == RoleCheck.Confirmed)
                {
                    if (await BanMessageAsync(account.Id, cancellation) is { } banned)
                    {
                        return Outcome.Reject(banned);
                    }

                    await store.TouchSessionAsync(session.Id, cancellation);
                    logger.Info($"{account.Name} logged in with the session of {session.DeviceName}");
                    return Outcome.Accept(account);
                }

                // RoleCheck.LoginAgain: the refresh token stopped working; a browser login decides.
            }
            else
            {
                logger.Debug($"Connection {request.ConnectionId} sent an unknown or expired session");
            }
        }

        return await DeviceLoginAsync(request, cancellation);
    }

    private async Task<Outcome> DeviceLoginAsync(LoginRequest request, CancellationToken cancellation)
    {
        var device = await keycloak.StartDeviceLoginAsync(cancellation);
        logger.Info($"Connection {request.ConnectionId} ({request.ClientUsername}) logs in in the browser, code {device.UserCode}");
        thread.Post(() => connections.Send(request.ConnectionId, new LoginRequired
        {
            VerificationUri = device.VerificationUriComplete,
            UserCode = device.UserCode,
            ExpiresInSeconds = (uint)device.ExpiresIn.TotalSeconds,
        }));

        var interval = device.Interval;
        var deadline = utcNow() + device.ExpiresIn;
        TokenResult result;
        while (true)
        {
            await _delay(interval, cancellation);
            result = await keycloak.PollDeviceLoginAsync(device.DeviceCode, cancellation);
            if (result.Status == TokenStatus.SlowDown)
            {
                interval += TimeSpan.FromSeconds(5);
            }
            else if (result.Status != TokenStatus.Pending)
            {
                break;
            }

            if (utcNow() > deadline)
            {
                result = new TokenResult(TokenStatus.Denied, Error: "expired");
                break;
            }
        }

        if (result.Status == TokenStatus.Denied)
        {
            logger.Info($"Browser login of connection {request.ConnectionId} ended: {result.Error}");
            return Outcome.Reject(BrowserLoginDenied);
        }

        var tokens = result.Tokens!;
        if (!tokens.Roles.Contains(config.PlayerRole))
        {
            logger.Info($"{tokens.Username} ({tokens.Sub}) logged in without the role {config.PlayerRole}");
            return Outcome.Reject(NotWhitelisted);
        }

        var account = await store.UpsertAccountAsync(tokens, cancellation);
        if (await BanMessageAsync(account.Id, cancellation) is { } banned)
        {
            return Outcome.Reject(banned);
        }

        var token = await store.CreateSessionAsync(account.Id, request.DeviceName, cancellation);
        logger.Info($"{account.Name} logged in in the browser; new session for {request.DeviceName}");
        thread.Post(() => connections.Send(request.ConnectionId, new SessionIssued { SessionToken = token }));
        return Outcome.Accept(account);
    }

    public enum RoleCheck
    {
        Confirmed,

        /// <summary>Keycloak says the user no longer has the role. Their sessions are deleted.</summary>
        Lost,

        /// <summary>The refresh token no longer works (offline session ended, user logged out everywhere). Sessions deleted.</summary>
        LoginAgain,

        /// <summary>Keycloak can't be reached and the last confirmation is too old.</summary>
        Unavailable,
    }

    /// <summary>
    /// Asks Keycloak whether the account still has the whitelist role, with its refresh token. When Keycloak can't be
    /// reached, a confirmation within <see cref="AccountsConfig.RoleCheckHours"/> still counts.
    /// </summary>
    public async Task<RoleCheck> CheckRoleAsync(Account account, CancellationToken cancellation)
    {
        if (account.RefreshToken == null)
        {
            await store.DeleteSessionsAsync(account.Id, cancellation);
            return RoleCheck.LoginAgain;
        }

        TokenResult result;
        try
        {
            result = await keycloak.RefreshAsync(account.RefreshToken, cancellation);
        }
        catch (Exception e) when (e is HttpRequestException or KeycloakException or TaskCanceledException &&
                                  !cancellation.IsCancellationRequested)
        {
            var recent = utcNow() - account.RoleCheckedAt < RoleCheckInterval;
            logger.Warn($"Could not check the role of {account.Name} with Keycloak ({e.Message}); " +
                        (recent ? $"last confirmed {account.RoleCheckedAt:u}, letting them in" : "the last check is too old"));
            return recent ? RoleCheck.Confirmed : RoleCheck.Unavailable;
        }

        if (result.Status == TokenStatus.Ok && result.Tokens!.Roles.Contains(config.PlayerRole))
        {
            await store.UpdateRoleCheckAsync(account.Id, result.Tokens.RefreshToken ?? account.RefreshToken, cancellation);
            return RoleCheck.Confirmed;
        }

        await store.DeleteSessionsAsync(account.Id, cancellation);
        await store.UpdateRoleCheckAsync(account.Id, null, cancellation);
        if (result.Status == TokenStatus.Ok)
        {
            logger.Info($"{account.Name} lost the role {config.PlayerRole}; deleted their sessions");
            return RoleCheck.Lost;
        }

        logger.Info($"The Keycloak refresh token of {account.Name} stopped working ({result.Error}); deleted their sessions");
        return RoleCheck.LoginAgain;
    }

    private async Task<string?> BanMessageAsync(Guid accountId, CancellationToken cancellation)
    {
        var ban = await store.ActiveBanAsync(accountId, cancellation);
        if (ban == null)
        {
            return null;
        }

        var until = ban.ExpiresAt is { } expires ? $" until {expires:yyyy-MM-dd HH:mm} UTC" : "";
        return $"You are banned from this server{until}. Reason: {ban.Reason}";
    }
}
