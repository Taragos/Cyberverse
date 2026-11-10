using Cyberverse.Server.NativeLayer;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using NLog;

namespace Cyberverse.Server;

public class GameServer: NativeGameServer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<ushort, HandleRawPacket> _packetHandlers = new();
    private readonly PacketWriter _writer = new();

    public readonly EntityService EntityService;
    public readonly EntityTracker EntityTracker;
    public readonly PlayerService PlayerService = new();

    public GameServer(ushort listeningPort) : base(listeningPort)
    {
        EntityTracker = new EntityTracker(this);
        EntityService = new EntityService();
    }

    /// <summary>
    /// Calls handler for every packet of type T. A packet that fails to read (truncated, oversized, unknown enum value,
    /// trailing bytes) is logged and dropped before it reaches the handler.
    /// </summary>
    public void AddPacketHandler<T>(HandlePacket<T> handler) where T : struct, IServerBoundPacket<T>
    {
        _packetHandlers.Add(T.MessageId, (frame, connectionId, body) =>
        {
            if (!Packet.TryRead<T>(body, out var packet))
            {
                Logger.Warn($"Dropped a malformed {typeof(T).Name} ({body.Length} bytes) from connection {connectionId}");
                return;
            }

            handler(this, (EMessageTypeServerbound)frame.MessageId, frame.Channel, connectionId, packet);
        });
    }

    protected override void OnConnectionStateChange(ESteamNetworkingConnectionState state, uint connectionId)
    {
        Logger.Trace($"{connectionId} changed state to {state}");

        // TODO: More handling of player connection states. Cleanup routines, pre-auth states, ...
        if (state is ESteamNetworkingConnectionState.ClosedByPeer or ESteamNetworkingConnectionState.ProblemDetectedLocally)
        {
            if (!PlayerService.ConnectedPlayers.TryGetValue(connectionId, out var player))
            {
                // someone connected but never signed in
                return;
            }
            
            Logger.Info($"Player {player.Name} disconnected");
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
            finally
            {
                ReleaseMessage(msg);
            }
        }
    }

    /// <summary>Sends a packet; its message id, channel and reliability come from the schema.</summary>
    public bool EnqueueMessage<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
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

        if (_packetHandlers.TryGetValue(frame.MessageId, out var handler))
        {
            handler(frame, message.ConnectionId, message.Span[MessageFrame.Size..]);
            return;
        }

        Logger.Warn($"No message handler for type {(EMessageTypeServerbound)frame.MessageId}");
    }
}
