using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Tests;

/// <summary>What the server does when a connection closes (GameServer calls ConnectionCleanup).</summary>
public class ConnectionCleanupTests
{
    private readonly PlayerService _players = new();
    private readonly EntityService _entities = new();
    private readonly EventBus _events = new();
    private readonly RecordingConnections _connections = new();
    private readonly EntityTracker _tracker;
    private readonly ConnectionCleanup _cleanup;
    private readonly List<string> _published = [];

    public ConnectionCleanupTests()
    {
        _tracker = new EntityTracker(_connections, _players, _entities);
        _cleanup = new ConnectionCleanup(_players, _entities, _tracker, _events);
        // Subscribers see the player still in the service, before anything is removed.
        _events.Subscribe<PlayerLeft>(e => _published.Add(
            $"left {e.Player.Name} online={_players.ConnectedPlayers.ContainsKey(e.Player.ConnectionId)} " +
            $"entities={_entities.SpawnedEntities.Count}"));
        _events.Subscribe<ConnectionClosed>(e => _published.Add($"closed {e.ConnectionId}"));
    }

    private sealed class RecordingConnections : IConnections
    {
        public readonly List<(uint Connection, object Packet)> Sent = [];

        public bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
        {
            Sent.Add((connectionId, packet));
            return true;
        }

        public bool Disconnect(uint connectionId, int reason, string debug) => true;
    }

    private Player AddPlayer(uint connectionId, string name)
    {
        var player = new Player { Name = name, ConnectionId = connectionId };
        _players.ConnectedPlayers.Add(connectionId, player);
        return player;
    }

    private Entity AddEntity(ulong id, uint owner)
    {
        var entity = new Entity(id, 0x1234) { NetworkIdOwner = owner };
        _entities.SpawnedEntities.Add(id, entity);
        return entity;
    }

    [Fact]
    public void AConnectionThatNeverLoggedInLosesItsEntities()
    {
        var watcher = AddPlayer(2, "bob");
        var leaked = AddEntity(10, owner: 1);
        AddEntity(11, owner: 2);
        _tracker.OnStartTrackingEntity(watcher, leaked);
        _connections.Sent.Clear();

        _cleanup.CleanUp(1);

        Assert.Equal([11ul], _entities.SpawnedEntities.Keys);
        var despawn = Assert.Single(_connections.Sent);
        Assert.Equal(2u, despawn.Connection);
        Assert.Equal(10ul, Assert.IsType<DestroyEntity>(despawn.Packet).NetworkedEntityId);
        Assert.Equal(["closed 1"], _published);
        Assert.Single(_players.Players);
    }

    [Fact]
    public void APlayerLeavesBeforeTheirEntitiesAndPlayerAreRemoved()
    {
        AddPlayer(1, "alice");
        AddPlayer(2, "bob");
        AddEntity(10, owner: 1);
        AddEntity(11, owner: 1);
        AddEntity(12, owner: 2);

        _cleanup.CleanUp(1);

        Assert.Equal(["left alice online=True entities=3", "closed 1"], _published);
        Assert.Equal([12ul], _entities.SpawnedEntities.Keys);
        Assert.Equal("bob", Assert.Single(_players.Players).Name);
    }

    [Fact]
    public void CleaningUpTwiceDoesNotLeaveTwice()
    {
        AddPlayer(1, "alice");

        _cleanup.CleanUp(1);
        _cleanup.CleanUp(1);

        Assert.Equal(["left alice online=True entities=0", "closed 1", "closed 1"], _published);
    }
}
