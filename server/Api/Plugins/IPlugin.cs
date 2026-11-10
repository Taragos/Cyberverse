using Microsoft.Extensions.DependencyInjection;

namespace Cyberverse.Server.Plugins;

/// <summary>
/// A server plugin. The server creates one instance per plugin with a public parameterless constructor, then, in the
/// order of <see cref="PluginInfo.DependsOn"/>: <see cref="ConfigureServices"/> for every plugin, then
/// <see cref="OnLoad"/>. At shutdown it calls <see cref="OnUnload"/> in reverse order. A plugin that throws is logged and
/// skipped (with the plugins depending on it); the server keeps running.
/// </summary>
public interface IPlugin
{
    PluginInfo Info { get; }

    /// <summary>Adds services other plugins can get from <see cref="IPluginContext.Services"/>. Called before any OnLoad.</summary>
    void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>
    /// Subscribes to events, registers packet handlers and commands. Register them through the context's
    /// <see cref="IPluginContext.Packets"/>, <see cref="IPluginContext.Events"/> and <see cref="IPluginContext.Commands"/>,
    /// so they are removed again when the plugin fails or unloads.
    /// </summary>
    void OnLoad(IPluginContext context);

    void OnUnload()
    {
    }
}

/// <summary>
/// Name: unique, lower case, also the config file's name (<c>config/&lt;name&gt;.json</c>). DependsOn: names of plugins
/// that must load first.
/// </summary>
public sealed record PluginInfo(string Name, string Version, params string[] DependsOn);
