using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.TweakDb;
using Cyberverse.Server.TweakDb.Generated;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using static Cyberverse.Server.Plugins.PluginHost;

namespace Cyberverse.Server.Tests;

/// <summary>A player's game joining the world, the character provider, and one character entity per player (CYB-00009).</summary>
public sealed class PlayerPacketHandlerTests : IDisposable
{
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cyberverse-tests-" + Guid.NewGuid());
    private readonly PlayerService _players = new();
    private readonly EntityService _entities = new();
    private readonly PacketDispatcher _packets;
    private readonly EventBus _events = new();
    private readonly RecordingConnections _connections = new();
    private readonly EntityTracker _tracker;
    private readonly List<string> _published = [];
    private IWorldService? _world;

    private static readonly Vector3 Home = new() { X = 100, Y = 200, Z = 10 };
    private static readonly Vector3 Saved = new() { X = 105, Y = 200, Z = 10 };

    public PlayerPacketHandlerTests()
    {
        _packets = new PacketDispatcher(_players.ConnectedPlayers.ContainsKey);
        _tracker = new EntityTracker(_connections, _players, _entities);
        _events.Subscribe<PlayerJoinedWorld>(e => _published.Add($"joined {e.Player.Name} at {e.Position.X}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, true);
        }
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

        public List<T> To<T>(uint connectionId) =>
            Sent.Where(x => x.Connection == connectionId).Select(x => x.Packet).OfType<T>().ToList();
    }

    private sealed class FakeProvider : ICharacterProvider
    {
        public readonly List<(string? Player, float X)> Joins = [];

        public void PlayerJoinedGame(Player player, Vector3 position) => Joins.Add((player.Name, position.X));
    }

    private void Start(ICharacterProvider? provider)
    {
        var services = new ServiceCollection()
            .AddSingleton(_players)
            .AddSingleton(_entities)
            .AddSingleton(_tracker)
            .AddSingleton<IConnections>(_connections)
            .AddSingleton<IPacketDispatcher>(_packets)
            .AddSingleton<IEventBus>(_events)
            .AddSingleton<ICommandRegistry>(new CommandRegistry());
        if (provider != null)
        {
            services.AddSingleton(provider);
        }

        var host = new PluginHost(services, _configDirectory);
        host.Add(new AuthPacketHandler(new LoginVersionCheck(ProtocolInfo.Version, "v1.0.0", [])), "test");
        host.Add(new PlayerPacketHandler(), "test");
        host.LoadAll();
        Assert.All(host.Plugins, x => Assert.Equal(PluginState.Loaded, x.State));
        _world = host.Services.GetRequiredService<IWorldService>();
    }

    private void AddPlayer(uint connectionId, string name) =>
        _players.ConnectedPlayers.Add(connectionId, new Player { Name = name, ConnectionId = connectionId });

    private void Send<T>(uint connectionId, T packet) where T : struct, IServerBoundPacket<T>
    {
        var writer = new PacketWriter();
        Packet.Write(writer, packet);
        Assert.Equal(DispatchResult.Handled, _packets.Dispatch(T.MessageId, connectionId, writer.WrittenSpan[MessageFrame.Size..]));
    }

    private void Join(uint connectionId, Vector3 at) => Send(connectionId, new PlayerJoinWorld { Position = at });

    private List<Entity> OwnedBy(uint connectionId) =>
        _entities.SpawnedEntities.Values.Where(x => x.NetworkIdOwner == connectionId).ToList();

    [Fact]
    public void WithoutAProviderAJoinSpawnsWhereTheGameLoaded()
    {
        Start(null);
        AddPlayer(1, "alice");

        Join(1, Home);

        var entity = Assert.Single(OwnedBy(1));
        Assert.Equal(Home, entity.WorldTransform);
        var spawned = Assert.Single(_connections.To<CharacterSpawned>(1));
        Assert.False(spawned.Teleport);
        Assert.Equal("alice", spawned.Name);
        Assert.Equal(["joined alice at 100"], _published);
    }

    [Fact]
    public void ASecondJoinLeavesOneCharacterEntity()
    {
        Start(null);
        AddPlayer(1, "alice");
        AddPlayer(2, "bob");
        Join(2, Home);
        Join(1, Home);
        var first = Assert.Single(OwnedBy(1));

        Join(1, Saved);

        var second = Assert.Single(OwnedBy(1));
        Assert.NotEqual(first.NetworkedEntityId, second.NetworkedEntityId);
        // Bob saw the old puppet go and the new one come.
        Assert.Contains(_connections.To<DestroyEntity>(2), x => x.NetworkedEntityId == first.NetworkedEntityId);
        Assert.Contains(_connections.To<SpawnEntity>(2), x => x.NetworkedEntityId == second.NetworkedEntityId);
    }

    [Fact]
    public void AReloadSendsTheNearbyPlayersAgain()
    {
        Start(null);
        AddPlayer(1, "alice");
        AddPlayer(2, "bob");
        Join(2, Home);
        var bob = Assert.Single(OwnedBy(2));

        Join(1, Home);
        Join(1, Home);

        // The reload dropped bob's puppet in alice's game, so she gets it twice.
        Assert.Equal(2, _connections.To<SpawnEntity>(1).Count(x => x.NetworkedEntityId == bob.NetworkedEntityId));
    }

    [Fact]
    public void WithAProviderNothingSpawnsUntilItPicksACharacter()
    {
        var provider = new FakeProvider();
        Start(provider);
        AddPlayer(1, "alice");

        Join(1, Home);

        Assert.Equal([("alice", 100f)], provider.Joins);
        Assert.Empty(OwnedBy(1));
        Assert.Null(_world!.CharacterOf(1));
        Assert.False(_world.TryGetTransform(1, out _, out _));

        Assert.True(_world.Spawn(1, new CharacterSpawn("c1", "Alex Example", Saved, 90, "{}")));

        var entity = Assert.Single(OwnedBy(1));
        Assert.Equal(Saved, entity.WorldTransform);
        var spawned = Assert.Single(_connections.To<CharacterSpawned>(1));
        Assert.True(spawned.Teleport);
        Assert.Equal(Saved, spawned.Position);
        Assert.Equal(90f, spawned.Yaw);
        Assert.Equal("{}", spawned.Appearance);
        Assert.Equal("c1", _world.CharacterOf(1)?.CharacterId);

        Send(1, new PlayerPositionUpdate { WorldTransform = Home, Yaw = 45 });
        Assert.True(_world.TryGetTransform(1, out var position, out var yaw));
        Assert.Equal(Home, position);
        Assert.Equal(45f, yaw);
    }

    [Fact]
    public void ACharacterSpawnsAsVWithItsLookRightAfterTheSpawn()
    {
        Start(new FakeProvider());
        AddPlayer(1, "alice");
        AddPlayer(2, "bob");
        Join(1, Home);
        Join(2, Home);
        // The tracker sends puppets to players who have a character of their own (it measures from there).
        Assert.True(_world!.Spawn(2, new CharacterSpawn("c2", "Bo Example", Home, 0, "")));
        const string look = "v1 body=f hairstyle=43 nose=6";

        Assert.True(_world!.Spawn(1, new CharacterSpawn("c1", "Alex Example", Saved, 0, look)));

        var entity = Assert.Single(OwnedBy(1));
        Assert.Equal((ulong)CyberverseRecords.RemoteVFemale, entity.RecordId);
        // Bob gets the puppet and then its look; alice gets nothing about her own puppet.
        var toBob = _connections.Sent.Where(x => x.Connection == 2).Select(x => x.Packet).ToList();
        var spawnAt = toBob.FindIndex(x => x is SpawnEntity spawn && spawn.NetworkedEntityId == entity.NetworkedEntityId);
        Assert.True(spawnAt >= 0);
        var sentLook = Assert.IsType<EntityLook>(toBob[spawnAt + 1]);
        Assert.Equal(entity.NetworkedEntityId, sentLook.NetworkedEntityId);
        Assert.Equal(look, sentLook.Appearance);
        Assert.DoesNotContain(_connections.To<EntityLook>(1), x => x.NetworkedEntityId == entity.NetworkedEntityId);
    }

    [Fact]
    public void AMaleOrEmptyLookSpawnsTheMaleV()
    {
        Assert.Equal(CyberverseRecords.RemoteVMale, PlayerPacketHandler.RecordFor("v1 body=m nose=1"));
        Assert.Equal(CyberverseRecords.RemoteVMale, PlayerPacketHandler.RecordFor(""));
        Assert.Equal(CyberverseRecords.RemoteVFemale, PlayerPacketHandler.RecordFor("v1 body=f"));
    }

    [Fact]
    public void RecordIdsFromNamesMatchTheGames()
    {
        // CyberverseRecords computes its ids from the names in client/tweaks/remote_v.yaml; this checks the scheme
        // against an id the game itself uses (the generated enum's cutscene V).
        Assert.Equal((ulong)Character.TPP_Player_Cutscene_No_Impostor_Male,
            RecordIdUtils.ToRecordId("Character.TPP_Player_Cutscene_No_Impostor_Male"));
    }

    [Fact]
    public void ANewLookReachesEveryoneWhoSeesTheCharacter()
    {
        Start(new FakeProvider());
        AddPlayer(1, "alice");
        AddPlayer(2, "bob");
        Join(1, Home);
        Join(2, Home);
        Assert.True(_world!.Spawn(2, new CharacterSpawn("c2", "Bo Example", Home, 0, "")));
        Assert.True(_world.Spawn(1, new CharacterSpawn("c1", "Alex Example", Saved, 0, "v1 body=m hairstyle=43")));
        var entity = Assert.Single(OwnedBy(1));

        Assert.True(_world.ChangeLook(1, "v1 body=m hairstyle=45"));

        Assert.Equal(["v1 body=m hairstyle=43", "v1 body=m hairstyle=45"],
            _connections.To<EntityLook>(2).Where(x => x.NetworkedEntityId == entity.NetworkedEntityId).Select(x => x.Appearance));
        Assert.Equal("v1 body=m hairstyle=45", _world.CharacterOf(1)?.Appearance);
        Assert.False(_world.ChangeLook(3, "v1 body=m")); // nobody on that connection
    }

    [Fact]
    public void SpawnNeedsAPlayerWhoseGameJoined()
    {
        Start(new FakeProvider());
        AddPlayer(1, "alice");

        Assert.False(_world!.Spawn(1, new CharacterSpawn("c1", "Alex Example", Saved, 0, "")));
        Assert.False(_world.Spawn(9, new CharacterSpawn("c1", "Alex Example", Saved, 0, "")));
        Assert.Empty(_entities.SpawnedEntities);
    }

    [Fact]
    public void AClosedConnectionForgetsItsCharacter()
    {
        Start(null);
        AddPlayer(1, "alice");
        Join(1, Home);

        _events.Publish(new ConnectionClosed(1));

        Assert.Null(_world!.CharacterOf(1));
        Assert.False(_world.Spawn(1, new CharacterSpawn("c1", "Alex Example", Saved, 0, "")));
    }
}
