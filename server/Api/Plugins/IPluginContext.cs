using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using NLog;

namespace Cyberverse.Server.Plugins;

/// <summary>What a plugin gets in <see cref="IPlugin.OnLoad"/>.</summary>
public interface IPluginContext
{
    PluginInfo Info { get; }

    /// <summary>The server's services (<see cref="Players.IPlayerService"/>, <see cref="IConnections"/>, ...) and those other plugins added.</summary>
    IServiceProvider Services { get; }

    /// <summary>A logger named after the plugin.</summary>
    Logger Logger { get; }

    /// <summary>Packet handlers registered here are removed when the plugin unloads.</summary>
    IPacketDispatcher Packets { get; }

    /// <summary>Subscriptions made here are removed when the plugin unloads.</summary>
    IEventBus Events { get; }

    /// <summary>Commands registered here are removed when the plugin unloads.</summary>
    ICommandRegistry Commands { get; }

    /// <summary>
    /// Reads <c>config/&lt;name&gt;.json</c> (camelCase keys, comments allowed, unknown keys are an error). When the file
    /// doesn't exist, it is written with the defaults of a new <typeparamref name="T"/>, which is returned.
    /// </summary>
    T LoadConfig<T>() where T : class, new();
}
