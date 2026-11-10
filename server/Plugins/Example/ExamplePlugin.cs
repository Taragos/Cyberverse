using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cyberverse.Plugin.Example;

/// <summary>Settings in config/example.json, written with these defaults on the first start.</summary>
public sealed class ExampleConfig
{
    public string Greeting { get; set; } = "Welcome to Night City, {0}!";
}

/// <summary>Greets players when they join the world and adds the console command "online".</summary>
public sealed class ExamplePlugin : IPlugin
{
    public PluginInfo Info { get; } = new("example", "1.0.0");

    public void OnLoad(IPluginContext context)
    {
        var config = context.LoadConfig<ExampleConfig>();
        var players = context.Services.GetRequiredService<IPlayerService>();

        // There is no chat yet (CYB-00016), so the greeting goes to the log.
        context.Events.Subscribe<PlayerJoinedWorld>(e => context.Logger.Info(string.Format(config.Greeting, e.Player.Name)));

        context.Commands.Register(new CommandDefinition("online", "Lists the players who are online", ctx =>
        {
            var names = players.Players.Select(p => p.Name).ToList();
            ctx.Sender.Reply(names.Count == 0 ? "Nobody is online" : $"{names.Count} online: {string.Join(", ", names)}");
        }));
    }
}
