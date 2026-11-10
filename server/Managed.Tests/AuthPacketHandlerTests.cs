using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using static Cyberverse.Server.Plugins.PluginHost;

namespace Cyberverse.Server.Tests;

/// <summary>The login: versions in InitAuth, then LoginWithSession through the login provider, then a player.</summary>
public sealed class AuthPacketHandlerTests : IDisposable
{
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cyberverse-tests-" + Guid.NewGuid());
    private readonly PlayerService _players = new();
    private readonly PacketDispatcher _packets;
    private readonly EventBus _events = new();
    private readonly FakeConnections _connections;
    private readonly List<string> _published = [];
    private DateTime _now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private AuthPacketHandler? _auth;

    public AuthPacketHandlerTests()
    {
        _packets = new PacketDispatcher(_players.ConnectedPlayers.ContainsKey);
        _connections = new FakeConnections(_players, _events);
        _events.Subscribe<PlayerAuthenticated>(e => _published.Add($"authenticated {e.Player.Name}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, true);
        }
    }

    /// <summary>Records what was sent; Disconnect removes the player and publishes ConnectionClosed like the server.</summary>
    private sealed class FakeConnections(PlayerService players, EventBus events) : IConnections
    {
        public readonly List<(uint Connection, object Packet)> Sent = [];
        public readonly List<(uint Connection, int Reason)> Closed = [];

        public bool Send<T>(uint connectionId, T packet) where T : struct, IClientBoundPacket<T>
        {
            Sent.Add((connectionId, packet));
            return true;
        }

        public bool Disconnect(uint connectionId, int reason, string debug)
        {
            Closed.Add((connectionId, reason));
            players.ConnectedPlayers.Remove(connectionId);
            events.Publish(new ConnectionClosed(connectionId));
            return true;
        }

        public InitAuthResult LastResult(uint connectionId) =>
            Sent.Where(x => x.Connection == connectionId).Select(x => x.Packet).OfType<InitAuthResult>().Last();
    }

    private sealed class FakeProvider : ILoginProvider
    {
        public readonly List<LoginRequest> Requests = [];
        public readonly List<uint> Cancelled = [];

        public void BeginLogin(LoginRequest request) => Requests.Add(request);

        public void CancelLogin(uint connectionId) => Cancelled.Add(connectionId);
    }

    private void Start(ILoginProvider? provider)
    {
        var services = new ServiceCollection()
            .AddSingleton(_players)
            .AddSingleton<IConnections>(_connections)
            .AddSingleton<IPacketDispatcher>(_packets)
            .AddSingleton<IEventBus>(_events)
            .AddSingleton<ICommandRegistry>(new CommandRegistry());
        if (provider != null)
        {
            services.AddSingleton(provider);
        }

        var host = new PluginHost(services, _configDirectory);
        _auth = new AuthPacketHandler(new LoginVersionCheck(ProtocolInfo.Version, "v1.0.0", []))
        {
            UtcNow = () => _now,
        };
        host.Add(_auth, "test");
        host.LoadAll();
        Assert.All(host.Plugins, x => Assert.Equal(PluginState.Loaded, x.State));
    }

    private ILoginService Logins => _auth!;

    private DispatchResult Send<T>(uint connectionId, T packet) where T : struct, IServerBoundPacket<T>
    {
        var writer = new PacketWriter();
        Packet.Write(writer, packet);
        return _packets.Dispatch(T.MessageId, connectionId, writer.WrittenSpan[MessageFrame.Size..]);
    }

    private void InitAuth(uint connectionId, string username = "winuser", string modVersion = "v1.0.0") =>
        Assert.Equal(DispatchResult.Handled, Send(connectionId, new InitAuth
        {
            ProtocolVersion = ProtocolInfo.Version, Username = username, ModVersion = modVersion, GameVersion = "2.31.0",
        }));

    private void LoginWithSession(uint connectionId, string token = "") =>
        Assert.Equal(DispatchResult.Handled, Send(connectionId, new LoginWithSession { SessionToken = token, DeviceName = "PC" }));

    [Fact]
    public void WithoutAProviderEveryoneGetsInUnderTheirUsername()
    {
        Start(null);

        InitAuth(1, "alice");
        Assert.Empty(_players.Players);
        LoginWithSession(1);

        var player = Assert.Single(_players.Players);
        Assert.Equal("alice", player.Name);
        Assert.Null(player.AccountId);
        Assert.Equal(EAuthResult.Ok, _connections.LastResult(1).AuthResult);
        Assert.Equal(["authenticated alice"], _published);
    }

    [Fact]
    public void AWrongVersionIsRejectedBeforeTheProvider()
    {
        var provider = new FakeProvider();
        Start(provider);

        InitAuth(1, modVersion: "v2.0.0");

        Assert.Equal(EAuthResult.VersionMismatch, _connections.LastResult(1).AuthResult);
        Assert.Equal([(1u, AuthPacketHandler.LoginRejectedReason)], _connections.Closed);
        Assert.Equal(DispatchResult.NotLoggedIn, Send(1, new PlayerUnmountCar()));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void TheProviderDecidesAndTheResultComesOnlyThen()
    {
        var provider = new FakeProvider();
        Start(provider);

        InitAuth(1, "winuser");
        LoginWithSession(1, "token-1");

        Assert.Equal([new LoginRequest(1, "token-1", "PC", "winuser")], provider.Requests);
        Assert.True(Logins.IsPending(1));
        Assert.Empty(_connections.Sent);

        Assert.True(Logins.Accept(1, new LoginIdentity("account-a", "Alice")));

        var player = Assert.Single(_players.Players);
        Assert.Equal(("Alice", "account-a"), (player.Name, player.AccountId));
        Assert.Equal(EAuthResult.Ok, _connections.LastResult(1).AuthResult);
        Assert.False(Logins.IsPending(1));
        Assert.False(Logins.Accept(1, new LoginIdentity("account-a", "Alice")));
    }

    [Fact]
    public void ARejectSendsTheMessageAndCloses()
    {
        var provider = new FakeProvider();
        Start(provider);
        InitAuth(1);
        LoginWithSession(1);

        Assert.True(Logins.Reject(1, "You are not on the whitelist."));

        var result = _connections.LastResult(1);
        Assert.Equal((EAuthResult.ValidationFailed, "You are not on the whitelist."), (result.AuthResult, result.Message));
        Assert.Equal([(1u, AuthPacketHandler.LoginRejectedReason)], _connections.Closed);
        Assert.Empty(_players.Players);
        Assert.Empty(provider.Cancelled);
        Assert.False(Logins.Reject(1, "again"));
    }

    [Fact]
    public void PacketsBeforeTheLoginAreDropped()
    {
        var provider = new FakeProvider();
        Start(provider);
        var spawned = 0;
        _packets.Register<PlayerSpawnCar>((_, _) => spawned++);

        Assert.Equal(DispatchResult.NotLoggedIn, Send(1, new PlayerSpawnCar()));
        InitAuth(1);
        LoginWithSession(1);
        Assert.Equal(DispatchResult.NotLoggedIn, Send(1, new PlayerSpawnCar()));
        Logins.Accept(1, new LoginIdentity("a", "A"));
        Assert.Equal(DispatchResult.Handled, Send(1, new PlayerSpawnCar()));

        Assert.Equal(1, spawned);
    }

    [Fact]
    public void ARepeatedInitAuthOrLoginIsIgnored()
    {
        var provider = new FakeProvider();
        Start(provider);

        InitAuth(1);
        InitAuth(1);
        LoginWithSession(1);
        LoginWithSession(1);
        Logins.Accept(1, new LoginIdentity("a", "A"));
        InitAuth(1);
        LoginWithSession(1);

        Assert.Single(provider.Requests);
        Assert.Single(_players.Players);
        Assert.Empty(_connections.Closed);
    }

    [Fact]
    public void LoginWithSessionWithoutInitAuthIsIgnored()
    {
        var provider = new FakeProvider();
        Start(provider);

        LoginWithSession(1);

        Assert.Empty(provider.Requests);
        Assert.False(Logins.IsPending(1));
    }

    [Fact]
    public void TheSameAccountLoggingInAgainKicksTheFirstConnection()
    {
        var provider = new FakeProvider();
        Start(provider);
        InitAuth(1);
        LoginWithSession(1);
        Logins.Accept(1, new LoginIdentity("account-a", "Alice"));

        InitAuth(2);
        LoginWithSession(2);
        Logins.Accept(2, new LoginIdentity("account-a", "Alice"));

        Assert.Equal([(1u, AuthPacketHandler.ReplacedReason)], _connections.Closed);
        Assert.Equal(2u, Assert.Single(_players.Players).ConnectionId);
    }

    [Fact]
    public void ClosingAPendingConnectionCancelsTheProvidersLogin()
    {
        var provider = new FakeProvider();
        Start(provider);
        InitAuth(1);
        InitAuth(2);
        LoginWithSession(2);

        _events.Publish(new ConnectionClosed(1));
        _events.Publish(new ConnectionClosed(2));

        Assert.Equal([2u], provider.Cancelled);
        Assert.False(Logins.IsPending(1));
        Assert.False(Logins.Accept(2, new LoginIdentity("a", "A")));
    }

    [Fact]
    public void ALoginThatTakesTooLongIsRejected()
    {
        var provider = new FakeProvider();
        Start(provider);
        InitAuth(1);
        LoginWithSession(1);

        _events.Publish(new ServerTick(_now + _auth!.Timeout));
        Assert.True(Logins.IsPending(1));
        _events.Publish(new ServerTick(_now + _auth.Timeout + TimeSpan.FromSeconds(1)));

        Assert.False(Logins.IsPending(1));
        Assert.Equal([1u], provider.Cancelled);
        Assert.Equal(EAuthResult.ValidationFailed, _connections.LastResult(1).AuthResult);
    }

    [Fact]
    public void AThrowingProviderRejectsTheLogin()
    {
        Start(new ThrowingProvider());
        InitAuth(1);
        LoginWithSession(1);

        Assert.Equal(EAuthResult.ValidationFailed, _connections.LastResult(1).AuthResult);
        Assert.Empty(_players.Players);
    }

    private sealed class ThrowingProvider : ILoginProvider
    {
        public void BeginLogin(LoginRequest request) => throw new InvalidOperationException("database down");

        public void CancelLogin(uint connectionId)
        {
        }
    }
}
