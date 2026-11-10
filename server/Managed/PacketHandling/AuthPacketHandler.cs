using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;
using NLog;

namespace Cyberverse.Server.PacketHandling;

public class AuthPacketHandler
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private PlayerService? _players = null;

    protected virtual void HandleInitAuth(GameServer server, EMessageTypeServerbound messageType, byte channelId, uint connectionId, InitAuth content)
    {
        Logger.Trace($"Connection request from {content.Username}");
            
        InitAuthResult resultPacket = default;
        resultPacket.ProtocolVersion = ProtocolInfo.Version;
            
        if (content.ProtocolVersion != ProtocolInfo.Version)
        {
            Logger.Info($"Rejected {content.Username}: protocol version 0x{content.ProtocolVersion:X8}, the server has 0x{ProtocolInfo.Version:X8}");
            resultPacket.AuthResult = EAuthResult.VersionMismatch;
        }
        else
        {
            //const auto validation = m_authController.ValidateUser(init_auth->username);
            var validation = true;
            resultPacket.AuthResult = validation ? EAuthResult.Ok : EAuthResult.ValidationFailed;
            // TODO: Check if there is an active player with that uuid already (that requires having non-username logins first, and also breaks my local dev for now!)
            _players!.ConnectedPlayers.Add(connectionId, new Player { Name = content.Username, ConnectionId = connectionId });
        }

        server.EnqueueMessage(connectionId, resultPacket);
    }

    public virtual void RegisterOnServer(GameServer server)
    {
        _players = server.PlayerService;
        server.AddPacketHandler<InitAuth>(HandleInitAuth);
    }
}