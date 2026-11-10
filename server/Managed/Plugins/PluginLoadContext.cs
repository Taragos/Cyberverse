using System.Reflection;
using System.Runtime.Loader;

namespace Cyberverse.Server.Plugins;

/// <summary>
/// One plugin's assemblies, so plugins can ship different versions of the same library. The API and what its types
/// expose (NLog, the DI abstractions) always come from the server, even when the plugin folder has a copy, so a plugin's
/// IPlugin is the server's IPlugin.
/// </summary>
internal sealed class PluginLoadContext(string mainAssemblyPath)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath))
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IPlugin).Assembly.GetName().Name!,
        typeof(NLog.Logger).Assembly.GetName().Name!,
        typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.GetName().Name!,
    };

    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedAssemblies.Contains(assemblyName.Name ?? ""))
        {
            return null; // the server's copy
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
