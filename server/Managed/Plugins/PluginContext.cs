using System.Text.Json;
using System.Text.Json.Serialization;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Cyberverse.Server.Protocol;
using NLog;

namespace Cyberverse.Server.Plugins;

/// <summary>A plugin's view of the server. Remembers what the plugin registered, so <see cref="RemoveRegistrations"/> can undo it.</summary>
internal sealed class PluginContext : IPluginContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly List<IDisposable> _registrations = [];
    private readonly string _configDirectory;

    public PluginContext(PluginInfo info, IServiceProvider services, string configDirectory,
        IPacketDispatcher packets, IEventBus events, ICommandRegistry commands)
    {
        Info = info;
        Services = services;
        _configDirectory = configDirectory;
        Logger = LogManager.GetLogger($"Plugin.{info.Name}");
        Packets = new ScopedPackets(packets, _registrations);
        Events = new ScopedEvents(events, _registrations);
        Commands = new ScopedCommands(commands, _registrations);
    }

    public PluginInfo Info { get; }
    public IServiceProvider Services { get; }
    public Logger Logger { get; }
    public IPacketDispatcher Packets { get; }
    public IEventBus Events { get; }
    public ICommandRegistry Commands { get; }

    public string ConfigPath => Path.Combine(_configDirectory, Info.Name + ".json");

    public T LoadConfig<T>() where T : class, new()
    {
        if (!File.Exists(ConfigPath))
        {
            var defaults = new T();
            Directory.CreateDirectory(_configDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaults, JsonOptions) + "\n");
            Logger.Info($"Wrote the default config to {ConfigPath}");
            return defaults;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(ConfigPath), JsonOptions)
               ?? throw new InvalidDataException($"{ConfigPath} is null");
    }

    /// <summary>Removes the plugin's handlers, subscriptions and commands, newest first.</summary>
    public void RemoveRegistrations()
    {
        for (var i = _registrations.Count - 1; i >= 0; i--)
        {
            _registrations[i].Dispose();
        }

        _registrations.Clear();
    }

    private static IDisposable Track(List<IDisposable> registrations, IDisposable registration)
    {
        registrations.Add(registration);
        return registration;
    }

    private sealed class ScopedPackets(IPacketDispatcher inner, List<IDisposable> registrations) : IPacketDispatcher
    {
        public IDisposable Register<T>(PacketHandler<T> handler) where T : struct, IServerBoundPacket<T> =>
            Track(registrations, inner.Register(handler));
    }

    private sealed class ScopedEvents(IEventBus inner, List<IDisposable> registrations) : IEventBus
    {
        public IDisposable Subscribe<T>(Action<T> handler) => Track(registrations, inner.Subscribe(handler));

        public void Publish<T>(T @event) => inner.Publish(@event);
    }

    private sealed class ScopedCommands(ICommandRegistry inner, List<IDisposable> registrations) : ICommandRegistry
    {
        public IDisposable Register(CommandDefinition command) => Track(registrations, inner.Register(command));

        public IReadOnlyCollection<CommandDefinition> Commands => inner.Commands;

        public CommandResult Execute(ICommandSender sender, string line) => inner.Execute(sender, line);
    }
}
