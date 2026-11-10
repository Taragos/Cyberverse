using System.Reflection;
using NLog;

namespace Cyberverse.Server.Plugins;

/// <summary>Finds plugins: every folder in the plugins directory holds one plugin assembly and its dependencies.</summary>
public static class PluginLoader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public sealed record Found(IPlugin Plugin, string Source);

    public sealed record Failure(string Source, string Error);

    /// <summary>
    /// For each folder plugins/&lt;folder&gt;/: loads &lt;folder&gt;.dll (or the folder's only dll) in its own load context
    /// and creates every public IPlugin class in it. Failures are logged and returned; they never throw.
    /// </summary>
    public static (List<Found> Found, List<Failure> Failed) Discover(string pluginsDirectory)
    {
        var found = new List<Found>();
        var failed = new List<Failure>();
        if (!Directory.Exists(pluginsDirectory))
        {
            Logger.Info($"No plugins directory at {Path.GetFullPath(pluginsDirectory)}");
            return (found, failed);
        }

        foreach (var folder in Directory.GetDirectories(pluginsDirectory).Order(StringComparer.Ordinal))
        {
            try
            {
                var plugins = LoadFolder(folder);
                found.AddRange(plugins.Select(plugin => new Found(plugin, folder)));
            }
            catch (Exception e)
            {
                var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                Logger.Error(error, $"Could not load the plugin in {folder}");
                failed.Add(new Failure(folder, error.Message));
            }
        }

        return (found, failed);
    }

    private static List<IPlugin> LoadFolder(string folder)
    {
        var assemblyPath = MainAssembly(folder);
        var context = new PluginLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

        var pluginTypes = assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(type))
            .ToList();
        if (pluginTypes.Count == 0)
        {
            var other = assembly.GetReferencedAssemblies().FirstOrDefault(x => x.Name == typeof(IPlugin).Assembly.GetName().Name);
            throw new InvalidOperationException(other == null
                ? $"{Path.GetFileName(assemblyPath)} has no public class implementing IPlugin"
                : $"{Path.GetFileName(assemblyPath)} has no public class implementing IPlugin (built against API {other.Version}, the server has {typeof(IPlugin).Assembly.GetName().Version})");
        }

        return pluginTypes.Select(type => (IPlugin)Activator.CreateInstance(type)!).ToList();
    }

    private static string MainAssembly(string folder)
    {
        var named = Path.Combine(folder, Path.GetFileName(folder) + ".dll");
        if (File.Exists(named))
        {
            return named;
        }

        var dlls = Directory.GetFiles(folder, "*.dll");
        return dlls.Length == 1
            ? dlls[0]
            : throw new FileNotFoundException($"Expected {Path.GetFileName(named)} (or exactly one .dll) in {folder}");
    }
}
