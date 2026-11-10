using Cyberverse.Plugin.Accounts.Keycloak;
using Cyberverse.Server;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Cyberverse.Server.Threading;
using Microsoft.Extensions.DependencyInjection;
using static Cyberverse.Server.Plugins.PluginHost;

namespace Cyberverse.Plugin.Accounts.Tests;

/// <summary>The plugin inside the server's plugin host, next to the real auth plugin.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AccountsPluginTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cyberverse-accounts-" + Guid.NewGuid());
    private readonly PlayerService _players = new();
    private readonly EventBus _events = new();
    private readonly ServerThread _thread = new();
    private readonly TestConnections _connections = new();
    private PacketDispatcher? _packets;

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, true);
        }
    }

    private PluginHost Load(string database, IKeycloak keycloak)
    {
        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(Path.Combine(_configDirectory, "accounts.json"),
            System.Text.Json.JsonSerializer.Serialize(new { database }));

        _packets = new PacketDispatcher(_players.ConnectedPlayers.ContainsKey);
        var services = new ServiceCollection()
            .AddSingleton(_players)
            .AddSingleton<IPlayerService>(_players)
            .AddSingleton<IConnections>(_connections)
            .AddSingleton<IPacketDispatcher>(_packets)
            .AddSingleton<IEventBus>(_events)
            .AddSingleton<ICommandRegistry>(new CommandRegistry())
            .AddSingleton<IServerThread>(_thread);
        var host = new PluginHost(services, _configDirectory);
        host.Add(new AuthPacketHandler(new LoginVersionCheck(ProtocolInfo.Version, "v1.0.0", [])), "test");
        host.Add(new AccountsPlugin { KeycloakFactory = _ => keycloak }, "test");
        host.LoadAll();
        return host;
    }

    private void Send<T>(uint connectionId, T packet) where T : struct, IServerBoundPacket<T>
    {
        var writer = new PacketWriter();
        Packet.Write(writer, packet);
        _packets!.Dispatch(T.MessageId, connectionId, writer.WrittenSpan[MessageFrame.Size..]);
    }

    private void Connect(uint connectionId, string sessionToken = "")
    {
        Send(connectionId, new InitAuth
        {
            ProtocolVersion = ProtocolInfo.Version, Username = "winuser", ModVersion = "v1.0.0", GameVersion = "2.31.0",
        });
        Send(connectionId, new LoginWithSession { SessionToken = sessionToken, DeviceName = "PC" });
    }

    private async Task<InitAuthResult> WaitForResult(uint connectionId)
    {
        for (var i = 0; i < 1000; i++)
        {
            _thread.RunPending();
            if (_connections.SentTo<InitAuthResult>(connectionId).Any())
            {
                return _connections.SentTo<InitAuthResult>(connectionId).Single();
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No InitAuthResult");
    }

    [Fact]
    public async Task WithoutADatabaseNobodyGetsIn()
    {
        var host = Load("Host=127.0.0.1;Port=1;Database=none;Timeout=2", new FakeKeycloak());
        Assert.Equal(PluginState.Failed, host.Plugins.Single(x => x.Info.Name == "accounts").State);

        Connect(1);
        var result = await WaitForResult(1);

        Assert.Equal(EAuthResult.ValidationFailed, result.AuthResult);
        Assert.Contains("unavailable", result.Message);
        Assert.Empty(_players.Players);
        host.UnloadAll();
    }

    [ContainerFact]
    public async Task APlayerLogsInThroughTheServer()
    {
        var keycloak = new FakeKeycloak();
        var host = Load(await postgres.NewDatabaseAsync(), keycloak);
        Assert.All(host.Plugins, x => Assert.Equal(PluginState.Loaded, x.State));

        Connect(1);
        while (keycloak.Started.IsEmpty)
        {
            _thread.RunPending();
            await Task.Delay(10);
        }

        keycloak.Approve("CODE-1", "sub-alice", "alice", "cyberverse-player");
        var result = await WaitForResult(1);

        Assert.Equal(EAuthResult.Ok, result.AuthResult);
        var player = Assert.Single(_players.Players);
        Assert.Equal("alice", player.Name);
        Assert.NotNull(player.AccountId);
        // The client got the link before, and the session token before the result.
        var order = _connections.Sent.Where(x => x.Connection == 1).Select(x => x.Packet.GetType().Name).ToList();
        Assert.Equal([nameof(LoginRequired), nameof(SessionIssued), nameof(InitAuthResult)], order);
        host.UnloadAll();
    }
}
