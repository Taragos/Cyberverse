using Cyberverse.Server.Commands;
using Cyberverse.Server.Events;
using Cyberverse.Server.Networking;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server.Plugins;

/// <summary>
/// Loads plugins in dependency order and unloads them in reverse. The services collection must already hold the core
/// services, including <see cref="IPacketDispatcher"/>, <see cref="IEventBus"/> and <see cref="ICommandRegistry"/>.
/// </summary>
public sealed class PluginHost(IServiceCollection services, string configDirectory)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public enum PluginState
    {
        Pending,
        Loaded,
        Failed,
        Unloaded,
    }

    public sealed class Entry(IPlugin plugin, PluginInfo info, string source)
    {
        public IPlugin Plugin { get; } = plugin;
        public PluginInfo Info { get; } = info;
        public string Source { get; } = source;
        public PluginState State { get; internal set; }
        public string? Error { get; internal set; }
        internal PluginContext? Context { get; set; }
    }

    private readonly List<Entry> _plugins = [];
    private readonly List<PluginLoader.Failure> _discoveryFailures = [];
    private ServiceProvider? _provider;

    /// <summary>Every plugin added, in load order once <see cref="LoadAll"/> ran.</summary>
    public IReadOnlyList<Entry> Plugins => _plugins;

    /// <summary>Plugin folders that couldn't be loaded at all.</summary>
    public IReadOnlyList<PluginLoader.Failure> DiscoveryFailures => _discoveryFailures;

    public IServiceProvider Services => _provider ?? throw new InvalidOperationException("LoadAll hasn't run");

    /// <param name="source">Where it came from, for the log: "built-in" or its folder.</param>
    public void Add(IPlugin plugin, string source)
    {
        try
        {
            _plugins.Add(new Entry(plugin, plugin.Info, source));
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Plugin {plugin.GetType().FullName} ({source}) not loaded: its Info threw");
            _discoveryFailures.Add(new PluginLoader.Failure(source, $"{plugin.GetType().FullName}.Info threw: {e.Message}"));
        }
    }

    /// <summary>Adds every plugin in the plugins directory (see <see cref="PluginLoader.Discover"/>).</summary>
    public void AddFromDirectory(string pluginsDirectory)
    {
        var (found, failed) = PluginLoader.Discover(pluginsDirectory);
        foreach (var plugin in found)
        {
            Add(plugin.Plugin, plugin.Source);
        }

        _discoveryFailures.AddRange(failed);
    }

    /// <summary>
    /// Orders the plugins by dependency, calls ConfigureServices on each, builds the service provider, then OnLoad on
    /// each. A plugin that fails (or whose dependency failed) is logged and skipped. Logs the result.
    /// </summary>
    public void LoadAll()
    {
        if (_provider != null)
        {
            throw new InvalidOperationException("LoadAll already ran");
        }

        Order();

        foreach (var entry in Pending())
        {
            var before = services.Count;
            try
            {
                entry.Plugin.ConfigureServices(services);
            }
            catch (Exception e)
            {
                // Undo what it added, so nothing resolves to a half-configured plugin.
                while (services.Count > before)
                {
                    services.RemoveAt(services.Count - 1);
                }

                Fail(entry, $"ConfigureServices threw: {e.Message}", e);
            }
        }

        _provider = services.BuildServiceProvider();
        var packets = _provider.GetRequiredService<IPacketDispatcher>();
        var events = _provider.GetRequiredService<IEventBus>();
        var commands = _provider.GetRequiredService<ICommandRegistry>();

        foreach (var entry in Pending())
        {
            if (FailedDependency(entry) is { } dependency)
            {
                Fail(entry, $"its dependency {dependency} failed");
                continue;
            }

            entry.Context = new PluginContext(entry.Info, _provider, configDirectory, packets, events, commands);
            try
            {
                entry.Plugin.OnLoad(entry.Context);
                entry.State = PluginState.Loaded;
            }
            catch (Exception e)
            {
                entry.Context.RemoveRegistrations();
                Fail(entry, $"OnLoad threw: {e.Message}", e);
            }
        }

        var loaded = _plugins.Where(x => x.State == PluginState.Loaded).ToList();
        Logger.Info(loaded.Count == 0
            ? "No plugins loaded"
            : $"Loaded {loaded.Count} plugin(s): {string.Join(", ", loaded.Select(x => $"{x.Info.Name} {x.Info.Version}"))}");
        var failedCount = _discoveryFailures.Count + _plugins.Count(x => x.State == PluginState.Failed);
        if (failedCount > 0)
        {
            Logger.Error($"{failedCount} plugin(s) failed to load, see above");
        }
    }

    /// <summary>Calls OnUnload on the loaded plugins in reverse load order and removes their registrations.</summary>
    public void UnloadAll()
    {
        foreach (var entry in Enumerable.Reverse(_plugins).Where(x => x.State == PluginState.Loaded))
        {
            try
            {
                entry.Plugin.OnUnload();
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Plugin {entry.Info.Name} threw in OnUnload");
            }

            entry.Context?.RemoveRegistrations();
            entry.State = PluginState.Unloaded;
        }

        _provider?.Dispose();
    }

    private IEnumerable<Entry> Pending() => _plugins.Where(x => x.State == PluginState.Pending).ToList();

    private string? FailedDependency(Entry entry) =>
        entry.Info.DependsOn.FirstOrDefault(name =>
            _plugins.FirstOrDefault(x => x.Info.Name == name) is not { State: PluginState.Loaded });

    /// <summary>
    /// Sorts _plugins so each comes after its dependencies, keeping the added order otherwise. Fails duplicates, plugins
    /// with a missing dependency and plugins in a cycle (and the ones depending on any of those).
    /// </summary>
    private void Order()
    {
        var byName = new Dictionary<string, Entry>();
        foreach (var entry in _plugins)
        {
            if (string.IsNullOrWhiteSpace(entry.Info.Name))
            {
                Fail(entry, "its PluginInfo has no name");
            }
            else if (!byName.TryAdd(entry.Info.Name, entry))
            {
                Fail(entry, $"the name {entry.Info.Name} is taken by {byName[entry.Info.Name].Source}");
            }
        }

        var ordered = new List<Entry>();
        var placed = new HashSet<Entry>();
        var remaining = _plugins.Where(x => x.State == PluginState.Pending).ToList();
        bool progress;
        do
        {
            progress = false;
            foreach (var entry in remaining.ToList())
            {
                var missing = entry.Info.DependsOn.FirstOrDefault(name => !byName.ContainsKey(name));
                var failed = entry.Info.DependsOn.FirstOrDefault(name => byName.TryGetValue(name, out var x) && x.State == PluginState.Failed);
                if (missing != null || failed != null)
                {
                    Fail(entry, missing != null ? $"it needs {missing}, which isn't installed" : $"its dependency {failed} failed");
                }
                else if (entry.Info.DependsOn.All(name => placed.Contains(byName[name])))
                {
                    ordered.Add(entry);
                    placed.Add(entry);
                }
                else
                {
                    continue;
                }

                remaining.Remove(entry);
                progress = true;
            }
        } while (progress);

        foreach (var entry in remaining)
        {
            Fail(entry, $"its dependencies form a cycle ({string.Join(", ", entry.Info.DependsOn)})");
        }

        var failedEntries = _plugins.Where(x => x.State == PluginState.Failed).ToList();
        _plugins.Clear();
        _plugins.AddRange(ordered);
        _plugins.AddRange(failedEntries);
    }

    private static void Fail(Entry entry, string error, Exception? exception = null)
    {
        entry.State = PluginState.Failed;
        entry.Error = error;
        Logger.Error(exception, $"Plugin {entry.Info.Name} ({entry.Source}) not loaded: {error}");
    }
}
