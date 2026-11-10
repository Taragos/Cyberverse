using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server.PacketHandling;

/// <summary>Built-in plugin: the login (InitAuth). Checks the versions and adds the player.</summary>
public class AuthPacketHandler(LoginVersionCheck versionCheck) : IPlugin
{
    /// <summary>The GameNetworkingSockets end reason for a rejected login (application reasons start at 1000).</summary>
    public const int LoginRejectedReason = 1001;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private PlayerService? _players = null;
    private IConnections? _connections = null;
    private IEventBus? _events = null;

    public PluginInfo Info { get; } = new("auth", BuildInfo.ModVersion);

    protected virtual void HandleInitAuth(uint connectionId, InitAuth content)
    {
        Logger.Trace($"Connection request from {content.Username}");

        if (_players!.ConnectedPlayers.TryGetValue(connectionId, out var existing))
        {
            Logger.Warn($"Ignored a second InitAuth from connection {connectionId} ({existing.Name})");
            return;
        }

        var result = versionCheck.Check(content.ProtocolVersion, content.ModVersion, content.GameVersion);
        InitAuthResult resultPacket = default;
        resultPacket.ProtocolVersion = ProtocolInfo.Version;
        resultPacket.ModVersion = BuildInfo.ModVersion;
        resultPacket.AuthResult = result.AuthResult;
        resultPacket.Message = result.Message;

        Player? player = null;
        if (result.Ok)
        {
            //const auto validation = m_authController.ValidateUser(init_auth->username);
            // TODO: Check if there is an active player with that uuid already (that requires having non-username logins first, and also breaks my local dev for now!)
            Logger.Info($"{content.Username} joined (connection {connectionId}, mod {content.ModVersion}, game {content.GameVersion})");
            player = new Player { Name = content.Username, ConnectionId = connectionId };
            _players.ConnectedPlayers.Add(connectionId, player);
        }

        _connections!.Send(connectionId, resultPacket);

        if (player != null)
        {
            _events!.Publish(new PlayerAuthenticated(player));
        }
        else
        {
            Logger.Info($"Rejected {content.Username} (connection {connectionId}): {result.LogReason}");
            _connections.Disconnect(connectionId, LoginRejectedReason, $"Login rejected: {result.AuthResult}");
        }
    }

    public void OnLoad(IPluginContext context)
    {
        _players = context.Services.GetRequiredService<PlayerService>();
        _connections = context.Services.GetRequiredService<IConnections>();
        _events = context.Events;
        context.Packets.Register<InitAuth>(HandleInitAuth);
    }
}
