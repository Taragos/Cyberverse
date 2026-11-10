using Cyberverse.Server.Events;
using Cyberverse.Server.NativeLayer;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using NLog;

namespace Cyberverse.Server;

public class GameServer : NativeGameServer, IConnections
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly PacketWriter _writer = new();
    private readonly PacketDispatcher _packets;
    private readonly IEventBus _events;

    public readonly EntityService EntityService;
    public readonly EntityTracker EntityTracker;
    public readonly PlayerService PlayerService;

    public GameServer(ushort listeningPort, PacketDispatcher packets, IEventBus events, PlayerService players,
        EntityService entities) : base(listeningPort)
    {
        _packets = packets;
        _events = events;
        PlayerService = players;
        EntityService = entities;
        EntityTracker = new EntityTracker(this);
    }

    protected override void OnConnectionStateChange(ESteamNetworkingConnectionState state, uint connectionId)
    {
        Logger.Trace($"{connectionId} changed state to {state}");

        // Called from native code: an exception must not get back there.
        try
        {
            // TODO: More handling of player connection states. Cleanup routines, pre-auth states, ...
            if (state is ESteamNetworkingConnectionState.ClosedByPeer or ESteamNetworkingConnectionState.ProblemDetectedLocally)
            {
                RemovePlayer(connectionId);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Handling the state change of connection {connectionId} to {state} threw");
        }
    }

    public bool Disconnect(uint connectionId, int reason, string debug) => CloseConnection(connectionId, reason, debug);

    /// <summary>Closes a connection from the server's side and cleans up after its player, if it had signed in.</summary>
    public override bool CloseConnection(uint connectionId, int reason, string debug)
    {
        var closed = base.CloseConnection(connectionId, reason, debug);
        RemovePlayer(connectionId);
        return closed;
    }

    private void RemovePlayer(uint connectionId)
    {
        if (!PlayerService.ConnectedPlayers.TryGetValue(connectionId, out var player))
        {
            // someone connected but never signed in
            return;
        }

        Logger.Info($"Player {player.Name} disconnected");
        _events.Publish(new PlayerLeft(player));

        var playerEntities = EntityService.SpawnedEntities
            .Select(x => x.Value)
            .Where(x => x.NetworkIdOwner == connectionId)
            .ToList();

        foreach (var playerEntity in playerEntities)
        {
            EntityTracker.StopTrackingOf(playerEntity);
            EntityService.RemoveEntity(playerEntity.NetworkedEntityId);
            EntityService.SpawnedEntities.Remove(playerEntity.NetworkedEntityId);
        }

        PlayerService.ConnectedPlayers.Remove(connectionId);
    }

    public override void Update(float deltaTime)
    {
        base.Update(deltaTime);
        for (var msg = PollIncomingMessage(); !msg.IsEmpty; msg = PollIncomingMessage())
        {
            try
            {
                ProcessMessage(msg);
            }
            catch (Exception e)
            {
                // Handlers' exceptions are caught by the dispatcher; this is for the framing itself.
                Logger.Error(e, $"Processing a message from connection {msg.ConnectionId} threw");
            }
            finally
            {
                ReleaseMessage(msg);
            }
        }
    }

    /// <summary>Sends a packet; its message id, channel and reliability come from the schema.</summary>
    public bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
    {
        _writer.Reset();
        Packet.Write(_writer, packet);
        return SendRaw(connectionId, _writer.WrittenSpan, T.Reliable);
    }

    protected virtual void ProcessMessage(Message message)
    {
        var reader = new PacketReader(message.Span);
        if (!MessageFrame.TryRead(ref reader, out var frame))
        {
            Logger.Warn($"Dropped a {message.Size} byte message without a frame from connection {message.ConnectionId}");
            return;
        }

        if (_packets.Dispatch(frame.MessageId, message.ConnectionId, message.Span[MessageFrame.Size..]) == DispatchResult.NoHandler)
        {
            Logger.Warn($"No message handler for type {(EMessageTypeServerbound)frame.MessageId}");
        }
    }
}
