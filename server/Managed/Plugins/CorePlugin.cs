using Cyberverse.Server.Commands;

namespace Cyberverse.Server.Plugins;

/// <summary>Built-in plugin: the console commands help, plugins and stop.</summary>
public sealed class CorePlugin(PluginHost host, Action stop) : IPlugin
{
    public PluginInfo Info { get; } = new("core", BuildInfo.ModVersion);

    public void OnLoad(IPluginContext context)
    {
        var commands = context.Commands;
        commands.Register(new CommandDefinition("help", "Lists the commands you may use", ctx =>
        {
            foreach (var command in commands.Commands.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (command.Permission == null || ctx.Sender.HasPermission(command.Permission))
                {
                    var usage = command.Usage.Length > 0 ? " " + command.Usage : "";
                    ctx.Sender.Reply($"{command.Name}{usage}: {command.Description}");
                }
            }
        }));

        commands.Register(new CommandDefinition("plugins", "Lists the plugins and whether they loaded", ctx =>
        {
            foreach (var plugin in host.Plugins)
            {
                var error = plugin.Error != null ? $": {plugin.Error}" : "";
                ctx.Sender.Reply($"{plugin.Info.Name} {plugin.Info.Version} ({plugin.Source}) {plugin.State}{error}");
            }

            foreach (var failure in host.DiscoveryFailures)
            {
                ctx.Sender.Reply($"{failure.Source} Failed: {failure.Error}");
            }
        }, Permission: "server.plugins"));

        commands.Register(new CommandDefinition("stop", "Unloads the plugins and stops the server", _ => stop(),
            Permission: "server.stop"));
    }
}
