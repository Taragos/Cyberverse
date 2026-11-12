// Imports glTF animations into existing .anims files with WolvenKit's own importer (CYB-00010).
//
// WolvenKit's command line can export .anims to glTF but imports glTF only as meshes, and without the game's
// archives, where the importer looks for the animation set's rig. This runs the same import the command line does
// (ConsoleFunctions.ImportTask with --keep), with the import format set to animations and the game's archives
// loaded. It uses WolvenKit's assemblies from its install folder through reflection, so it builds without them.
//
// For every <name>.anims.glb under the raw folder, the .anims at the same relative path under the depot folder is
// rebuilt: animations are matched by name, and the importer matches the glTF joints to the rig's bones by name (it
// refuses joints the rig doesn't have).
//
//   AnimImport <wolvenkit folder> <game folder> <raw folder> <depot folder>
using System.Reflection;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: AnimImport <wolvenkit folder> <game folder> <raw folder> <depot folder>");
    return 2;
}

var wolvenKit = args[0];
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var path = Path.Combine(wolvenKit, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};

// The import arguments are bound from the configuration, which includes the environment.
Environment.SetEnvironmentVariable("GltfImportArgs__ImportFormat", "Anims");
// The host insists on its appsettings.json next to the running program.
File.Copy(Path.Combine(wolvenKit, "appsettings.json"), Path.Combine(AppContext.BaseDirectory, "appsettings.json"), true);

Type Find(string assembly, string type) =>
    Assembly.LoadFrom(Path.Combine(wolvenKit, assembly)).GetType(type) ?? throw new InvalidOperationException($"No {type} in {assembly}");

const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var builder = Find("WolvenKit.CLI.dll", "WolvenKit.CLI.GenericHost").GetMethod("CreateHostBuilder", Static)!
    .Invoke(null, [Array.Empty<string>()])!;
var host = builder.GetType().GetMethod("Build")!.Invoke(builder, null)!;
var services = (IServiceProvider)host.GetType().GetProperty("Services")!.GetValue(host)!;

var archives = services.GetService(Find("WolvenKit.Common.dll", "WolvenKit.Common.IArchiveManager"))!;
archives.GetType().GetMethod("LoadGameArchives", [typeof(FileInfo)])!
    .Invoke(archives, [new FileInfo(Path.Combine(args[1], "bin", "x64", "Cyberpunk2077.exe"))]);

var functions = services.GetService(Find("WolvenKit.Modkit.dll", "CP77Tools.Tasks.ConsoleFunctions"))!;
var import = (Task<int>)functions.GetType().GetMethod("ImportTask")!
    .Invoke(functions, [new FileSystemInfo[] { new DirectoryInfo(args[2]) }, new DirectoryInfo(args[3]), true])!;
return await import;
