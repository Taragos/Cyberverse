using System.Collections.Concurrent;
using Cyberverse.Plugin.Characters.Data;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Threading;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;
using static Cyberverse.Server.Plugins.PluginHost;

namespace Cyberverse.Plugin.Characters.Tests;

/// <summary>The plugin inside the server's plugin host, next to the real auth and player-sync plugins.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class CharactersPluginTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cyberverse-characters-" + Guid.NewGuid());
    private readonly PlayerService _players = new();
    private readonly EntityService _entities = new();
    private readonly EventBus _events = new();
    private readonly ServerThread _thread = new();
    private readonly TestConnections _connections = new();
    private readonly CommandRegistry _commands = new();
    private PacketDispatcher? _packets;
    private ConnectionCleanup? _cleanup;
    private PluginHost? _host;
    private CharacterStore? _store;
    private DateTime _now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Vector3 GameLoaded = new(100, 200, 10);
    private static readonly Vector3 Walked = new(-1500, 300, 25);

    public void Dispose()
    {
        _host?.UnloadAll();
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, true);
        }
    }

    private sealed class TestConnections : IConnections
    {
        public readonly ConcurrentQueue<(uint Connection, object Packet)> Sent = new();

        public bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
        {
            Sent.Enqueue((connectionId, packet));
            return true;
        }

        public bool Disconnect(uint connectionId, int reason, string debug) => true;

        public List<T> To<T>(uint connectionId) =>
            Sent.Where(x => x.Connection == connectionId).Select(x => x.Packet).OfType<T>().ToList();
    }

    private sealed class Replies : ICommandSender
    {
        public readonly List<string> Lines = [];
        public string Name => "test";
        public void Reply(string message) => Lines.Add(message);
        public bool HasPermission(string permission) => true;
    }

    private async Task LoadAsync(int maxCharacters = 3)
    {
        var database = await postgres.NewDatabaseAsync();
        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(Path.Combine(_configDirectory, "characters.json"),
            System.Text.Json.JsonSerializer.Serialize(new { database, maxCharacters, saveIntervalSeconds = 60 }));

        _packets = new PacketDispatcher(_players.ConnectedPlayers.ContainsKey);
        var tracker = new EntityTracker(_connections, _players, _entities);
        _cleanup = new ConnectionCleanup(_players, _entities, tracker, _events);
        var services = new ServiceCollection()
            .AddSingleton(_players)
            .AddSingleton<IPlayerService>(_players)
            .AddSingleton(_entities)
            .AddSingleton(tracker)
            .AddSingleton<IConnections>(_connections)
            .AddSingleton<IPacketDispatcher>(_packets)
            .AddSingleton<IEventBus>(_events)
            .AddSingleton<ICommandRegistry>(_commands)
            .AddSingleton<IServerThread>(_thread);
        _host = new PluginHost(services, _configDirectory);
        _host.Add(new AuthPacketHandler(new LoginVersionCheck(ProtocolInfo.Version, "v1.0.0", [])), "test");
        _host.Add(new PlayerPacketHandler(), "test");
        var plugin = new CharactersPlugin { UtcNow = () => _now };
        _host.Add(plugin, "test");
        _host.LoadAll();
        Assert.All(_host.Plugins, x => Assert.Equal(PluginState.Loaded, x.State));
        _store = plugin.Store;
    }

    private void Login(uint connectionId, string account) => _players.ConnectedPlayers.Add(connectionId,
        new Player { Name = account, AccountId = account, ConnectionId = connectionId });

    private void Send<T>(uint connectionId, T packet) where T : struct, IServerBoundPacket<T>
    {
        var writer = new PacketWriter();
        Packet.Write(writer, packet);
        _packets!.Dispatch(T.MessageId, connectionId, writer.WrittenSpan[MessageFrame.Size..]);
    }

    /// <summary>Runs the server thread until a new packet of type T was sent to the connection.</summary>
    private async Task<T> Next<T>(uint connectionId)
    {
        var before = _connections.To<T>(connectionId).Count;
        for (var i = 0; i < 1000; i++)
        {
            _thread.RunPending();
            var sent = _connections.To<T>(connectionId);
            if (sent.Count > before)
            {
                return sent[^1];
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No {typeof(T).Name} for connection {connectionId}");
    }

    private async Task<CharacterList> JoinGame(uint connectionId)
    {
        var list = Next<CharacterList>(connectionId);
        Send(connectionId, new PlayerJoinWorld { Position = GameLoaded });
        return await list;
    }

    private async Task<CharacterList> Create(uint connectionId, string first, string last, string appearance = "")
    {
        var list = Next<CharacterList>(connectionId);
        Send(connectionId, new CharacterCreate { FirstName = first, LastName = last, Appearance = appearance });
        return await list;
    }

    private async Task<string> CreateFails(uint connectionId, string first, string last)
    {
        var failed = Next<CharacterRequestFailed>(connectionId);
        Send(connectionId, new CharacterCreate { FirstName = first, LastName = last, Appearance = "" });
        return (await failed).Message;
    }

    private async Task<CharacterSpawned> Select(uint connectionId, string characterId)
    {
        var spawned = Next<CharacterSpawned>(connectionId);
        Send(connectionId, new CharacterSelect { CharacterId = characterId });
        return await spawned;
    }

    /// <summary>What GameServer does when a connection closes; then waits for the plugin's writes.</summary>
    private void Disconnect(uint connectionId)
    {
        _cleanup!.CleanUp(connectionId);
    }

    private async Task<Character> Stored(string account, string name)
    {
        for (var i = 0; i < 100; i++)
        {
            var found = (await _store!.SearchAsync(name)).SingleOrDefault(x => x.AccountId == account);
            if (found != null)
            {
                return found;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No {name}");
    }

    [ContainerFact]
    public async Task APlayerCreatesListsAndDeletesCharacters()
    {
        await LoadAsync();
        Login(1, "acc-1");

        var empty = await JoinGame(1);
        Assert.Empty(empty.Characters);
        Assert.Equal(3, empty.MaxCharacters);

        await Create(1, "Alex", "Example", "{\"hair\":3}");
        var two = await Create(1, "  Sam", "Sample ");
        Assert.Equal(["Alex Example", "Sam Sample"], two.Characters.Select(x => x.Name).Order());
        Assert.All(two.Characters, x => Assert.Equal(0, x.LastPlayedUnix));

        var alex = two.Characters.Single(x => x.Name == "Alex Example");
        var afterDelete = Next<CharacterList>(1);
        Send(1, new CharacterDelete { CharacterId = alex.CharacterId });
        Assert.Equal(["Sam Sample"], (await afterDelete).Characters.Select(x => x.Name));

        // Soft delete: the row stays for admins, and the name is free again.
        var deleted = Assert.Single(await _store!.SearchAsync("Alex Example"));
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal(2, (await Create(1, "alex", "EXAMPLE")).Characters.Length);
    }

    [ContainerFact]
    public async Task NamesAreUniqueIgnoringCaseAcrossAccounts()
    {
        await LoadAsync();
        Login(1, "acc-1");
        Login(2, "acc-2");
        await JoinGame(1);
        await JoinGame(2);

        await Create(1, "Alex", "Example");

        Assert.Equal("The name aLEX example is taken.", await CreateFails(2, "aLEX", "example"));
        Assert.Contains("letters", await CreateFails(2, "Al3x", "Example"));
    }

    [ContainerFact]
    public async Task AnAccountHasAtMostMaxCharacters()
    {
        await LoadAsync(maxCharacters: 2);
        Login(1, "acc-1");
        await JoinGame(1);
        await Create(1, "Alex", "Example");
        var list = await Create(1, "Sam", "Sample");

        Assert.Equal("You already have 2 characters. Delete one first.", await CreateFails(1, "Kim", "Third"));

        var afterDelete = Next<CharacterList>(1);
        Send(1, new CharacterDelete { CharacterId = list.Characters[0].CharacterId });
        await afterDelete;
        Assert.Equal(2, (await Create(1, "Kim", "Third")).Characters.Length);
    }

    [ContainerFact]
    public async Task ThePositionIsSavedOnDisconnectAndRestoredOnSelect()
    {
        await LoadAsync();
        Login(1, "acc-1");
        await JoinGame(1);
        var alex = (await Create(1, "Alex", "Example", "{\"hair\":3}")).Characters.Single();

        // A new character stays where the game loaded.
        var first = await Select(1, alex.CharacterId);
        Assert.False(first.Teleport);
        Assert.Equal(GameLoaded, first.Position);
        Assert.Equal("{\"hair\":3}", first.Appearance);
        Assert.Single(_entities.SpawnedEntities.Values, x => x.NetworkIdOwner == 1);

        Send(1, new PlayerPositionUpdate { Sequence = 1, WorldTransform = Walked, Yaw = 270 });
        Disconnect(1);
        var saved = await WaitFor(() => Stored("acc-1", "Alex Example"), c => c.PositionX != null);
        Assert.Equal((Walked.X, Walked.Y, Walked.Z, 270f), (saved.PositionX, saved.PositionY, saved.PositionZ, saved.Yaw));

        // Next login: the list shows when it was last played, and selecting teleports to where the character left.
        Login(2, "acc-1");
        var list = await JoinGame(2);
        Assert.Equal(new DateTimeOffset(_now).ToUnixTimeSeconds(), list.Characters.Single().LastPlayedUnix);
        var back = await Select(2, alex.CharacterId);
        Assert.True(back.Teleport);
        Assert.Equal(Walked, back.Position);
        Assert.Equal(270f, back.Yaw);
    }

    [ContainerFact]
    public async Task PositionsAreSavedEverySaveInterval()
    {
        await LoadAsync();
        Login(1, "acc-1");
        await JoinGame(1);
        var alex = (await Create(1, "Alex", "Example")).Characters.Single();
        await Select(1, alex.CharacterId);
        Send(1, new PlayerPositionUpdate { Sequence = 1, WorldTransform = Walked, Yaw = 90 });

        _events.Publish(new ServerTick(_now));
        var saved = await WaitFor(() => Stored("acc-1", "Alex Example"), c => c.PositionX != null);
        Assert.Equal(Walked.X, saved.PositionX);

        Send(1, new PlayerPositionUpdate { Sequence = 2, WorldTransform = GameLoaded, Yaw = 90 });
        _events.Publish(new ServerTick(_now.AddSeconds(30))); // not due yet
        await Task.Delay(200);
        Assert.Equal(Walked.X, (await Stored("acc-1", "Alex Example")).PositionX);

        _events.Publish(new ServerTick(_now.AddSeconds(61)));
        Assert.Equal(GameLoaded.X, (await WaitFor(() => Stored("acc-1", "Alex Example"), c => c.PositionX == GameLoaded.X)).PositionX);
    }

    [ContainerFact]
    public async Task AReloadPutsTheCharacterBackWithoutTheSelectScreen()
    {
        await LoadAsync();
        Login(1, "acc-1");
        await JoinGame(1);
        var alex = (await Create(1, "Alex", "Example")).Characters.Single();
        await Select(1, alex.CharacterId);
        Send(1, new PlayerPositionUpdate { Sequence = 1, WorldTransform = Walked, Yaw = 10 });
        var lists = _connections.To<CharacterList>(1).Count;

        var again = Next<CharacterSpawned>(1);
        Send(1, new PlayerJoinWorld { Position = GameLoaded });
        var spawned = await again;

        Assert.True(spawned.Teleport);
        Assert.Equal(Walked, spawned.Position);
        Assert.Equal(lists, _connections.To<CharacterList>(1).Count);
        Assert.Single(_entities.SpawnedEntities.Values, x => x.NetworkIdOwner == 1);
    }

    [ContainerFact]
    public async Task OnlyYourOwnCharactersAndOnlyOnTheSelectScreen()
    {
        await LoadAsync();
        Login(1, "acc-1");
        Login(2, "acc-2");
        await JoinGame(1);
        await JoinGame(2);
        var alex = (await Create(1, "Alex", "Example")).Characters.Single();

        var failed = Next<CharacterRequestFailed>(2);
        Send(2, new CharacterSelect { CharacterId = alex.CharacterId });
        Assert.Equal("That character doesn't exist.", (await failed).Message);
        failed = Next<CharacterRequestFailed>(2);
        Send(2, new CharacterDelete { CharacterId = alex.CharacterId });
        Assert.Equal("That character doesn't exist.", (await failed).Message);

        await Select(1, alex.CharacterId);
        Assert.Equal("You are already playing a character.", await CreateFails(1, "Sam", "Sample"));
    }

    [ContainerFact]
    public async Task AnAdminRestoresADeletedCharacter()
    {
        await LoadAsync();
        Login(1, "acc-1");
        await JoinGame(1);
        var alex = (await Create(1, "Alex", "Example")).Characters.Single();
        var afterDelete = Next<CharacterList>(1);
        Send(1, new CharacterDelete { CharacterId = alex.CharacterId });
        await afterDelete;
        await Create(1, "Alex", "Example"); // takes the name meanwhile

        Assert.Contains("name is taken", await Command($"restore-character {alex.CharacterId}"));
        Assert.Equal($"Restored {alex.CharacterId}", await Command($"restore-character {alex.CharacterId} Alex Again"));

        var list = await _store!.ListAsync("acc-1");
        Assert.Equal(["Alex Again", "Alex Example"], list.Select(x => x.Name).Order());
        Assert.Contains(await CommandLines("characters acc-1"), x => x.Contains("Alex Again"));
    }

    private async Task<string> Command(string line) => (await CommandLines(line)).Single();

    private async Task<List<string>> CommandLines(string line)
    {
        var replies = new Replies();
        Assert.Equal(CommandResult.Ran, _commands.Execute(replies, line));
        for (var i = 0; i < 500 && replies.Lines.Count == 0; i++)
        {
            _thread.RunPending();
            await Task.Delay(10);
        }

        return replies.Lines;
    }

    private static async Task<T> WaitFor<T>(Func<Task<T>> get, Func<T, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var value = await get();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition not met");
    }
}
