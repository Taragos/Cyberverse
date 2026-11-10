using System.Runtime.Loader;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Services;
using Cyberverse.Server.Types;
using Microsoft.Extensions.DependencyInjection;

namespace Cyberverse.Server.Tests;

public sealed class PluginLoaderTests : IDisposable
{
    private const string Example = "Cyberverse.Plugin.Example";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyberverse-tests-" + Guid.NewGuid());
    private string Plugins => Path.Combine(_root, "plugins");
    private string Config => Path.Combine(_root, "config");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>Installs the example plugin like an admin would, plus a stray copy of the API it must not use.</summary>
    private void InstallExample(string folder = Example)
    {
        var target = Path.Combine(Plugins, folder);
        Directory.CreateDirectory(target);
        foreach (var file in new[] { $"{Example}.dll", "Cyberverse.Server.Api.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(target, file));
        }
    }

    private sealed class Sender : ICommandSender
    {
        public readonly List<string> Replies = [];
        public string Name => "test";
        public void Reply(string message) => Replies.Add(message);
        public bool HasPermission(string permission) => true;
    }

    [Fact]
    public void LoadsAPluginFromItsFolderInItsOwnContext()
    {
        InstallExample();

        var (found, failed) = PluginLoader.Discover(Plugins);

        Assert.Empty(failed);
        var plugin = Assert.Single(found).Plugin;
        Assert.Equal("example", plugin.Info.Name);
        var context = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Same(typeof(IPlugin).Assembly, AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "Cyberverse.Server.Api"));
    }

    [Fact]
    public void TheExamplePluginRegistersACommandAndGreetsJoiningPlayers()
    {
        InstallExample();
        var players = new PlayerService();
        players.ConnectedPlayers.Add(1, new Player { Name = "Bob", ConnectionId = 1 });
        var commands = new CommandRegistry();
        var events = new EventBus();
        var services = new ServiceCollection()
            .AddSingleton<IPlayerService>(players)
            .AddSingleton<IPacketDispatcher>(new PacketDispatcher())
            .AddSingleton<IEventBus>(events)
            .AddSingleton<ICommandRegistry>(commands);
        var host = new PluginHost(services, Config);
        host.AddFromDirectory(Plugins);

        host.LoadAll();
        events.Publish(new PlayerJoinedWorld(players.ConnectedPlayers[1], default));
        var sender = new Sender();
        commands.Execute(sender, "online");

        Assert.Equal(PluginHost.PluginState.Loaded, Assert.Single(host.Plugins).State);
        Assert.Equal(["1 online: Bob"], sender.Replies);
        Assert.True(File.Exists(Path.Combine(Config, "example.json")));
    }

    [Fact]
    public void ReportsBrokenFoldersWithoutThrowing()
    {
        Directory.CreateDirectory(Path.Combine(Plugins, "empty"));
        var junk = Path.Combine(Plugins, "junk");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "junk.dll"), "not an assembly");
        var noPlugin = Path.Combine(Plugins, "no-plugin");
        Directory.CreateDirectory(noPlugin);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Cyberverse.Server.Api.dll"), Path.Combine(noPlugin, "no-plugin.dll"));
        InstallExample();

        var (found, failed) = PluginLoader.Discover(Plugins);

        Assert.Single(found);
        Assert.Equal(["empty", "junk", "no-plugin"], failed.Select(x => Path.GetFileName(x.Source)));
    }

    [Fact]
    public void AMissingPluginsDirectoryIsEmpty()
    {
        var (found, failed) = PluginLoader.Discover(Plugins);
        Assert.Empty(found);
        Assert.Empty(failed);
    }
}
