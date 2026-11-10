using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Cyberverse.Server.Commands;
using Cyberverse.Server.Config;
using Cyberverse.Server.Events;
using Cyberverse.Server.NativeLayer;
using Cyberverse.Server.Networking;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Players;
using Cyberverse.Server.Plugins;
using Cyberverse.Server.Protocol;
using Cyberverse.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Cyberverse.Server;

public class Program
{
    /// <summary>One folder per plugin, relative to the working directory.</summary>
    public const string PluginsDirectory = "plugins";

    /// <summary>Plugin configs (config/&lt;plugin&gt;.json), relative to the working directory.</summary>
    public const string ConfigDirectory = "config";

    static void Main(string[] args)
    {
        InitLogging();
        var logger = LogManager.GetCurrentClassLogger();
        logger.Info($"Starting Cyberverse Server {BuildInfo.ModVersion}, protocol 0x{ProtocolInfo.Version:X8} (c) 2023-2025 MeFisto94");
        var config = ServerConfig.Load(ServerConfig.FileName, Environment.GetEnvironmentVariable);
        logger.Info(config.AllowedGameVersions.Length == 0
            ? "Allowing any game version"
            : $"Allowing game versions {string.Join(", ", config.AllowedGameVersions)}");
        var players = new PlayerService();
        var entities = new EntityService();
        var packets = new PacketDispatcher();
        var events = new EventBus();
        var commands = new CommandRegistry();
        var server = new GameServer(1337, packets, events, players, entities);

        var services = new ServiceCollection()
            .AddSingleton(config)
            .AddSingleton(server)
            .AddSingleton<IConnections>(server)
            .AddSingleton(players)
            .AddSingleton<IPlayerService>(players)
            .AddSingleton(entities)
            .AddSingleton(server.EntityTracker)
            .AddSingleton<IPacketDispatcher>(packets)
            .AddSingleton<IEventBus>(events)
            .AddSingleton<ICommandRegistry>(commands);

        var stopping = new CancellationTokenSource();
        var host = new PluginHost(services, ConfigDirectory);
        host.Add(new CorePlugin(host, stopping.Cancel), "built-in");
        var versionCheck = new LoginVersionCheck(ProtocolInfo.Version, BuildInfo.ModVersion, config.AllowedGameVersions);
        host.Add(new AuthPacketHandler(versionCheck), "built-in");
        host.Add(new PlayerPacketHandler(), "built-in");
        host.AddFromDirectory(PluginsDirectory);
        host.LoadAll();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        var consoleLines = StartConsoleReader();
        var console = new ConsoleCommandSender();
        logger.Info("Type \"help\" for the console commands");

        while (!stopping.IsCancellationRequested)
        {
            while (consoleLines.TryDequeue(out var line))
            {
                if (line.Trim().Length > 0)
                {
                    commands.Execute(console, line);
                }
            }

            server.Update(0.1f);
            Thread.Sleep(100);
        }

        logger.Info("Stopping");
        host.UnloadAll();
        Native.destroy_gameserver();
    }

    /// <summary>Reads console lines on a background thread; the main loop runs them, so commands run on the server thread.</summary>
    private static ConcurrentQueue<string> StartConsoleReader()
    {
        var lines = new ConcurrentQueue<string>();
        new Thread(() =>
        {
            // ReadLine returns null at the end of input, e.g. in a container without stdin.
            while (Console.ReadLine() is { } line)
            {
                lines.Enqueue(line);
            }
        }) { IsBackground = true, Name = "Console" }.Start();
        return lines;
    }

    private static void InitLogging()
    {
        NLog.Config.LoggingConfiguration config = new NLog.Config.LoggingConfiguration();
        var logFile = new NLog.Targets.FileTarget("logfile")
        {
            CreateDirs = true,
            FileName = "logs/log.txt",
            ArchiveOldFileOnStartup = true,
            ArchiveEvery = NLog.Targets.FileArchivePeriod.Hour,
            ArchiveAboveSize = 100000000,
            EnableArchiveFileCompression = true,
            MaxArchiveDays = 30
        };
        var logConsole = new NLog.Targets.ColoredConsoleTarget("logconsole")
        {
            EnableAnsiOutput = true, // Maybe this improves the Open Game Panel(?)
            DetectOutputRedirected = true // Disables colors when a file is detected
        };

        #if DEBUG
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, logConsole);
        #else
        config.AddRule(LogLevel.Info, LogLevel.Fatal, logConsole);
        #endif
        config.AddRule(LogLevel.Warn, LogLevel.Fatal, logFile);

        LogManager.Configuration = config;
    }
}
