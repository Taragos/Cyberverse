using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;
using NLog;

namespace Cyberverse.Server.PacketHandling;

public class AuthPacketHandler(LoginVersionCheck versionCheck)
{
    /// <summary>The GameNetworkingSockets end reason for a rejected login (application reasons start at 1000).</summary>
    public const int LoginRejectedReason = 1001;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private PlayerService? _players = null;

    protected virtual void HandleInitAuth(GameServer server, EMessageTypeServerbound messageType, byte channelId, uint connectionId, InitAuth content)
    {
        Logger.Trace($"Connection request from {content.Username}");

        var result = versionCheck.Check(content.ProtocolVersion, content.ModVersion, content.GameVersion);
        InitAuthResult resultPacket = default;
        resultPacket.ProtocolVersion = ProtocolInfo.Version;
        resultPacket.ModVersion = BuildInfo.ModVersion;
        resultPacket.AuthResult = result.AuthResult;
        resultPacket.Message = result.Message;

        if (result.Ok)
        {
            //const auto validation = m_authController.ValidateUser(init_auth->username);
            // TODO: Check if there is an active player with that uuid already (that requires having non-username logins first, and also breaks my local dev for now!)
            Logger.Info($"{content.Username} joined (connection {connectionId}, mod {content.ModVersion}, game {content.GameVersion})");
            _players!.ConnectedPlayers.Add(connectionId, new Player { Name = content.Username, ConnectionId = connectionId });
        }

        server.EnqueueMessage(connectionId, resultPacket);

        if (!result.Ok)
        {
            Logger.Info($"Rejected {content.Username} (connection {connectionId}): {result.LogReason}");
            server.CloseConnection(connectionId, LoginRejectedReason, $"Login rejected: {result.AuthResult}");
        }
    }

    public virtual void RegisterOnServer(GameServer server)
    {
        _players = server.PlayerService;
        server.AddPacketHandler<InitAuth>(HandleInitAuth);
    }
}
