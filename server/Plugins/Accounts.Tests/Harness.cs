using System.Collections.Concurrent;
using Cyberverse.Plugin.Accounts.Data;
using Cyberverse.Plugin.Accounts.Keycloak;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Threading;
using NLog;

namespace Cyberverse.Plugin.Accounts.Tests;

/// <summary>Posted actions wait for <see cref="Pump"/>, like on the server thread.</summary>
public sealed class TestThread : IServerThread
{
    private readonly ConcurrentQueue<Action> _actions = new();

    public void Post(Action action) => _actions.Enqueue(action);

    public void Pump()
    {
        while (_actions.TryDequeue(out var action))
        {
            action();
        }
    }
}

public sealed class TestConnections : IConnections
{
    public readonly ConcurrentQueue<(uint Connection, object Packet)> Sent = new();
    public readonly ConcurrentQueue<(uint Connection, int Reason)> Closed = new();

    public bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
    {
        Sent.Enqueue((connectionId, packet));
        return true;
    }

    public bool Disconnect(uint connectionId, int reason, string debug)
    {
        Closed.Enqueue((connectionId, reason));
        return true;
    }

    public IEnumerable<T> SentTo<T>(uint connectionId) =>
        Sent.Where(x => x.Connection == connectionId).Select(x => x.Packet).OfType<T>();
}

/// <summary>Every connection is pending; records the decisions.</summary>
public sealed class TestLogins : ILoginService
{
    public readonly Dictionary<uint, LoginIdentity> Accepted = new();
    public readonly Dictionary<uint, string> Rejected = new();

    public TimeSpan Timeout => TimeSpan.FromMinutes(15);

    public bool IsPending(uint connectionId) => !Accepted.ContainsKey(connectionId) && !Rejected.ContainsKey(connectionId);

    public bool Accept(uint connectionId, LoginIdentity identity) => Accepted.TryAdd(connectionId, identity);

    public bool Reject(uint connectionId, string message) => Rejected.TryAdd(connectionId, message);
}

/// <summary>A Keycloak whose users the test controls: device logins wait until the test approves them.</summary>
public sealed class FakeKeycloak : IKeycloak
{
    private readonly ConcurrentDictionary<string, TokenResult> _devices = new();
    private readonly ConcurrentDictionary<string, UserTokens> _refreshTokens = new();
    private int _counter;

    public readonly ConcurrentQueue<string> Started = new();
    public bool Unreachable { get; set; }
    public int Refreshes;

    public Task<DeviceAuthorization> StartDeviceLoginAsync(CancellationToken cancellation)
    {
        ThrowIfUnreachable();
        var code = $"CODE-{Interlocked.Increment(ref _counter)}";
        _devices[code] = new TokenResult(TokenStatus.Pending);
        Started.Enqueue(code);
        return Task.FromResult(new DeviceAuthorization(code, code, $"https://login.example/device?user_code={code}",
            TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)));
    }

    public Task<TokenResult> PollDeviceLoginAsync(string deviceCode, CancellationToken cancellation)
    {
        ThrowIfUnreachable();
        return Task.FromResult(_devices[deviceCode]);
    }

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken cancellation)
    {
        ThrowIfUnreachable();
        Interlocked.Increment(ref Refreshes);
        if (!_refreshTokens.TryGetValue(refreshToken, out var user))
        {
            return Task.FromResult(new TokenResult(TokenStatus.Denied, Error: "invalid_grant"));
        }

        // Rotates the refresh token, as Keycloak does.
        var rotated = user with { RefreshToken = $"refresh-{Guid.NewGuid():N}" };
        _refreshTokens[rotated.RefreshToken!] = rotated;
        return Task.FromResult(new TokenResult(TokenStatus.Ok, rotated));
    }

    /// <summary>The player logged in in the browser as this user.</summary>
    public void Approve(string deviceCode, string sub, string username, params string[] roles)
    {
        var tokens = new UserTokens(sub, username, roles.ToHashSet(), $"refresh-{Guid.NewGuid():N}");
        _refreshTokens[tokens.RefreshToken!] = tokens;
        _devices[deviceCode] = new TokenResult(TokenStatus.Ok, tokens);
    }

    public void Deny(string deviceCode) => _devices[deviceCode] = new TokenResult(TokenStatus.Denied, Error: "access_denied");

    /// <summary>Changes the roles Keycloak reports for the user from now on.</summary>
    public void SetRoles(string sub, params string[] roles)
    {
        foreach (var (token, user) in _refreshTokens.Where(x => x.Value.Sub == sub).ToList())
        {
            _refreshTokens[token] = user with { Roles = roles.ToHashSet() };
        }
    }

    /// <summary>The user's offline sessions ended (logged out everywhere, disabled): refresh tokens stop working.</summary>
    public void EndSessions(string sub)
    {
        foreach (var token in _refreshTokens.Where(x => x.Value.Sub == sub).Select(x => x.Key).ToList())
        {
            _refreshTokens.TryRemove(token, out _);
        }
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
        {
            throw new HttpRequestException("Connection refused (keycloak:8080)");
        }
    }
}

/// <summary>A <see cref="LoginFlow"/> with test doubles around it and a clock the test moves.</summary>
public sealed class Harness
{
    public readonly TestThread Thread = new();
    public readonly TestConnections Connections = new();
    public readonly TestLogins Logins = new();
    public readonly AccountsConfig Config;
    public readonly AccountStore Store;
    public readonly LoginFlow Flow;
    public DateTime Now = DateTime.UtcNow;

    public Harness(string database, IKeycloak keycloak, AccountsConfig? config = null, bool realDelays = false)
    {
        Config = config ?? new AccountsConfig { Database = database };
        Store = new AccountStore(AccountsDbContext.Options(database), TimeSpan.FromDays(Config.SessionLifetimeDays), () => Now);
        Flow = new LoginFlow(Store, keycloak, Config, Logins, Connections, Thread, LogManager.GetLogger("test"), () => Now,
            realDelays ? null : (_, cancellation) => Task.Delay(5, cancellation));
    }

    public void Begin(uint connectionId, string sessionToken = "", string device = "PC") =>
        Flow.BeginLogin(new LoginRequest(connectionId, sessionToken, device, "winuser"));

    /// <summary>Pumps the server thread until the condition holds.</summary>
    public async Task WaitUntil(Func<bool> condition, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            Thread.Pump();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The login didn't get there in time");
            }

            await Task.Delay(10);
        }
    }

    public Task WaitForDecision(uint connectionId, int timeoutSeconds = 60) =>
        WaitUntil(() => !Logins.IsPending(connectionId), timeoutSeconds);

    public async Task<LoginRequired> WaitForLoginRequired(uint connectionId)
    {
        await WaitUntil(() => Connections.SentTo<LoginRequired>(connectionId).Any());
        return Connections.SentTo<LoginRequired>(connectionId).Single();
    }

    public string IssuedToken(uint connectionId) => Connections.SentTo<SessionIssued>(connectionId).Single().SessionToken;
}
