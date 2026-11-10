using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server.PacketHandling;

/// <summary>
/// Built-in plugin: the login. InitAuth checks the versions; LoginWithSession then hands the connection to the
/// <see cref="ILoginProvider"/> (the accounts plugin), which accepts or rejects it. Only then the connection gets a
/// player and an InitAuthResult. Without a provider everyone gets in under their client username (development mode).
/// </summary>
public class AuthPacketHandler(LoginVersionCheck versionCheck) : IPlugin, ILoginService
{
    /// <summary>The GameNetworkingSockets end reason for a rejected login (application reasons start at 1000).</summary>
    public const int LoginRejectedReason = 1001;

    /// <summary>The end reason for a player whose account logged in again on another connection.</summary>
    public const int ReplacedReason = 1002;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<uint, PendingLogin> _pending = new();
    private PlayerService? _players = null;
    private IConnections? _connections = null;
    private IEventBus? _events = null;
    private ILoginProvider? _provider = null;

    public PluginInfo Info { get; } = new("auth", BuildInfo.ModVersion);

    /// <summary>Browser login included; the device code alone lives 10 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The clock for the timeout; the server's ticks carry the time, this is only for the start.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    private sealed class PendingLogin(string username, DateTime since)
    {
        public string Username { get; } = username;
        public DateTime Since { get; } = since;

        /// <summary>LoginWithSession arrived and the provider is working on it.</summary>
        public bool Started { get; set; }
    }

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ILoginService>(this);

    public void OnLoad(IPluginContext context)
    {
        _players = context.Services.GetRequiredService<PlayerService>();
        _connections = context.Services.GetRequiredService<IConnections>();
        _events = context.Events;
        _provider = context.Services.GetService<ILoginProvider>();
        if (_provider == null)
        {
            Logger.Warn("No login provider (accounts plugin): development mode, everyone gets in under their client " +
                        "username and nothing is verified");
        }

        context.Packets.AllowBeforeLogin<InitAuth>();
        context.Packets.AllowBeforeLogin<LoginWithSession>();
        context.Packets.Register<InitAuth>(HandleInitAuth);
        context.Packets.Register<LoginWithSession>(HandleLoginWithSession);
        context.Events.Subscribe<ConnectionClosed>(e => Cancel(e.ConnectionId));
        context.Events.Subscribe<ServerTick>(e => RejectTimedOut(e.UtcNow));
    }

    protected virtual void HandleInitAuth(uint connectionId, InitAuth content)
    {
        Logger.Trace($"Connection request from {content.Username}");

        if (_players!.ConnectedPlayers.TryGetValue(connectionId, out var existing) || _pending.ContainsKey(connectionId))
        {
            Logger.Warn($"Ignored a second InitAuth from connection {connectionId} ({existing?.Name ?? content.Username})");
            return;
        }

        var result = versionCheck.Check(content.ProtocolVersion, content.ModVersion, content.GameVersion);
        if (!result.Ok)
        {
            Logger.Info($"Rejected {content.Username} (connection {connectionId}): {result.LogReason}");
            SendResultAndClose(connectionId, result.AuthResult, result.Message);
            return;
        }

        Logger.Debug($"Versions of {content.Username} accepted (connection {connectionId}, mod {content.ModVersion}, " +
                     $"game {content.GameVersion}), waiting for the login");
        _pending.Add(connectionId, new PendingLogin(content.Username, UtcNow()));
    }

    protected virtual void HandleLoginWithSession(uint connectionId, LoginWithSession content)
    {
        if (!_pending.TryGetValue(connectionId, out var pending) || pending.Started)
        {
            Logger.Debug($"Ignored LoginWithSession from connection {connectionId}: no login waiting for it");
            return;
        }

        pending.Started = true;
        if (_provider == null)
        {
            AddPlayer(connectionId, pending.Username, null);
            return;
        }

        try
        {
            _provider.BeginLogin(new LoginRequest(connectionId, content.SessionToken, content.DeviceName, pending.Username));
        }
        catch (Exception e)
        {
            Logger.Error(e, $"The login provider threw on connection {connectionId}");
            Reject(connectionId, "The server could not check your login. Try again later.");
        }
    }

    public bool IsPending(uint connectionId) => _pending.ContainsKey(connectionId);

    public bool Accept(uint connectionId, LoginIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_pending.ContainsKey(connectionId))
        {
            return false;
        }

        AddPlayer(connectionId, identity.DisplayName, identity.AccountId);
        return true;
    }

    public bool Reject(uint connectionId, string message)
    {
        if (!_pending.Remove(connectionId, out var pending))
        {
            return false;
        }

        Logger.Info($"Rejected {pending.Username} (connection {connectionId}): {message}");
        SendResultAndClose(connectionId, EAuthResult.ValidationFailed, message);
        return true;
    }

    private void AddPlayer(uint connectionId, string name, string? accountId)
    {
        _pending.Remove(connectionId);

        if (accountId != null)
        {
            // A crashed client's connection can linger until it times out, so the new login wins.
            var previous = _players!.ConnectedPlayers.Values.FirstOrDefault(p => p.AccountId == accountId);
            if (previous != null)
            {
                Logger.Info($"{name} logged in again on connection {connectionId}, closing connection {previous.ConnectionId}");
                _connections!.Disconnect(previous.ConnectionId, ReplacedReason, "Logged in from somewhere else");
                _players.ConnectedPlayers.Remove(previous.ConnectionId);
            }
        }

        var player = new Player { Name = name, AccountId = accountId, ConnectionId = connectionId };
        _players!.ConnectedPlayers.Add(connectionId, player);
        Logger.Info(accountId == null
            ? $"{name} joined (connection {connectionId}, development mode)"
            : $"{name} joined (connection {connectionId}, account {accountId})");

        InitAuthResult result = default;
        result.ProtocolVersion = ProtocolInfo.Version;
        result.ModVersion = BuildInfo.ModVersion;
        result.AuthResult = EAuthResult.Ok;
        result.Message = "";
        _connections!.Send(connectionId, result);
        _events!.Publish(new PlayerAuthenticated(player));
    }

    private void SendResultAndClose(uint connectionId, EAuthResult authResult, string message)
    {
        InitAuthResult result = default;
        result.ProtocolVersion = ProtocolInfo.Version;
        result.ModVersion = BuildInfo.ModVersion;
        result.AuthResult = authResult;
        result.Message = message;
        _connections!.Send(connectionId, result);
        _connections.Disconnect(connectionId, LoginRejectedReason, $"Login rejected: {authResult}");
    }

    private void Cancel(uint connectionId)
    {
        if (_pending.Remove(connectionId, out var pending) && pending.Started)
        {
            _provider?.CancelLogin(connectionId);
        }
    }

    private void RejectTimedOut(DateTime now)
    {
        var timedOut = _pending.Where(x => now - x.Value.Since > Timeout).Select(x => x.Key).ToList();
        foreach (var connectionId in timedOut)
        {
            if (_pending[connectionId].Started)
            {
                _provider?.CancelLogin(connectionId);
            }

            Reject(connectionId, "The login took too long. Connect again to retry.");
        }
    }
}
