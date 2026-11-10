using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Microsoft.Extensions.DependencyInjection;
using static Cyberverse.Server.Plugins.PluginHost;

namespace Cyberverse.Server.Tests;

public sealed class PluginHostTests : IDisposable
{
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cyberverse-tests-" + Guid.NewGuid());
    private readonly List<string> _log = [];
    private readonly PacketDispatcher _packets = new();
    private readonly EventBus _events = new();
    private readonly CommandRegistry _commands = new();

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, true);
        }
    }

    private sealed class TestPlugin(string name, List<string> log, params string[] dependsOn) : IPlugin
    {
        public PluginInfo Info { get; } = new(name, "1.0", dependsOn);
        public Action<IServiceCollection>? Configure;
        public Action<IPluginContext>? Load;

        public void ConfigureServices(IServiceCollection services)
        {
            log.Add($"configure {name}");
            Configure?.Invoke(services);
        }

        public void OnLoad(IPluginContext context)
        {
            log.Add($"load {name}");
            Load?.Invoke(context);
        }

        public void OnUnload() => log.Add($"unload {name}");
    }

    private PluginHost Host(params TestPlugin[] plugins)
    {
        var services = new ServiceCollection()
            .AddSingleton<IPacketDispatcher>(_packets)
            .AddSingleton<IEventBus>(_events)
            .AddSingleton<ICommandRegistry>(_commands);
        var host = new PluginHost(services, _configDirectory);
        foreach (var plugin in plugins)
        {
            host.Add(plugin, "test");
        }

        return host;
    }

    private TestPlugin Plugin(string name, params string[] dependsOn) => new(name, _log, dependsOn);

    private static PluginState State(PluginHost host, string name) => host.Plugins.Single(x => x.Info.Name == name).State;

    [Fact]
    public void LoadsAfterDependenciesAndUnloadsInReverse()
    {
        var host = Host(Plugin("chat", "accounts"), Plugin("admin", "chat", "accounts"), Plugin("accounts"));

        host.LoadAll();
        host.UnloadAll();

        Assert.Equal([
            "configure accounts", "configure chat", "configure admin",
            "load accounts", "load chat", "load admin",
            "unload admin", "unload chat", "unload accounts",
        ], _log);
    }

    [Fact]
    public void SkipsPluginsWithMissingFailedOrCyclicDependencies()
    {
        var broken = Plugin("broken");
        broken.Load = _ => throw new InvalidOperationException("boom");
        var host = Host(Plugin("needs-ghost", "ghost"), broken, Plugin("needs-broken", "broken"),
            Plugin("a", "b"), Plugin("b", "a"), Plugin("fine"), Plugin("fine"));

        host.LoadAll();

        Assert.Equal(PluginState.Failed, State(host, "needs-ghost"));
        Assert.Equal(PluginState.Failed, State(host, "broken"));
        Assert.Equal(PluginState.Failed, State(host, "needs-broken"));
        Assert.Equal(PluginState.Failed, State(host, "a"));
        Assert.Equal(PluginState.Failed, State(host, "b"));
        Assert.Equal([PluginState.Loaded, PluginState.Failed], host.Plugins.Where(x => x.Info.Name == "fine").Select(x => x.State));
        Assert.Contains("ghost", host.Plugins.Single(x => x.Info.Name == "needs-ghost").Error);
    }

    [Fact]
    public void AFailedOnLoadRemovesWhatThePluginRegistered()
    {
        var broken = Plugin("broken");
        broken.Load = context =>
        {
            context.Commands.Register(new CommandDefinition("half", "", _ => { }));
            context.Packets.Register<PlayerUnmountCar>((_, _) => { });
            context.Events.Subscribe<PlayerLeft>(_ => { });
            throw new InvalidOperationException("boom");
        };
        var host = Host(broken);

        host.LoadAll();

        Assert.Empty(_commands.Commands);
        Assert.Equal(DispatchResult.NoHandler, _packets.Dispatch(PlayerUnmountCar.MessageId, 1, []));
    }

    [Fact]
    public void UnloadRemovesRegistrationsAndPluginsShareServices()
    {
        var provider = Plugin("provider");
        provider.Configure = services => services.AddSingleton(new List<string> { "shared" });
        var user = Plugin("user", "provider");
        user.Load = context =>
        {
            Assert.Equal("shared", context.Services.GetRequiredService<List<string>>().Single());
            context.Commands.Register(new CommandDefinition("hello", "", _ => { }));
        };
        var host = Host(provider, user);

        host.LoadAll();
        Assert.Single(_commands.Commands);
        host.UnloadAll();

        Assert.Empty(_commands.Commands);
        Assert.All(host.Plugins, x => Assert.Equal(PluginState.Unloaded, x.State));
    }

    [Fact]
    public void AFailedConfigureServicesAddsNoServices()
    {
        var broken = Plugin("broken");
        broken.Configure = services =>
        {
            services.AddSingleton(new List<string>());
            throw new InvalidOperationException("boom");
        };
        var host = Host(broken);

        host.LoadAll();

        Assert.Null(host.Services.GetService<List<string>>());
        Assert.Equal(PluginState.Failed, State(host, "broken"));
    }

    public sealed class Settings
    {
        public string Greeting { get; set; } = "hi";
        public int Limit { get; set; } = 3;
    }

    [Fact]
    public void LoadConfigWritesDefaultsThenReadsTheFile()
    {
        Settings? first = null, second = null;
        var plugin = Plugin("greeter");
        plugin.Load = context => first = context.LoadConfig<Settings>();
        Host(plugin).LoadAll();

        var path = Path.Combine(_configDirectory, "greeter.json");
        Assert.Contains("\"greeting\": \"hi\"", File.ReadAllText(path));
        File.WriteAllText(path, "{ \"greeting\": \"hey\", // comment\n }");

        var again = Plugin("greeter");
        again.Load = context => second = context.LoadConfig<Settings>();
        Host(again).LoadAll();

        Assert.Equal("hi", first!.Greeting);
        Assert.Equal("hey", second!.Greeting);
        Assert.Equal(3, second.Limit);
    }

    [Fact]
    public void AConfigWithAnUnknownKeyFailsThePlugin()
    {
        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(Path.Combine(_configDirectory, "greeter.json"), "{ \"greting\": \"typo\" }");
        var plugin = Plugin("greeter");
        plugin.Load = context => context.LoadConfig<Settings>();
        var host = Host(plugin);

        host.LoadAll();

        Assert.Equal(PluginState.Failed, State(host, "greeter"));
    }
}
