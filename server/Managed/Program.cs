using System.Diagnostics;
using Cyberverse.Server.NativeLayer;
using Cyberverse.Server.Config;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Protocol;
using NLog;

namespace Cyberverse.Server;

public class Program
{
    static void Main(string[] args)
    {
        InitLogging();
        var logger = LogManager.GetCurrentClassLogger();
        logger.Info($"Starting Cyberverse Server {BuildInfo.ModVersion}, protocol 0x{ProtocolInfo.Version:X8} (c) 2023-2025 MeFisto94");
        var config = ServerConfig.Load(ServerConfig.FileName, Environment.GetEnvironmentVariable);
        logger.Info(config.AllowedGameVersions.Length == 0
            ? "Allowing any game version"
            : $"Allowing game versions {string.Join(", ", config.AllowedGameVersions)}");
        var server = new GameServer(1337);
        AddTypicalPacketHandlers(server, config);
        
        var quit = false;

        while (!quit)
        {
            var sw = new Stopwatch();
            sw.Start();
            server.Update(0.1f);
            sw.Stop();
            //Console.WriteLine($"Update took {sw.ElapsedMilliseconds}ms");
            Thread.Sleep(100);
        }

        Native.destroy_gameserver();
    }

    private static void AddTypicalPacketHandlers(GameServer server, ServerConfig config)
    {
        // TODO: Store the auth handler somewhere
        var versionCheck = new LoginVersionCheck(ProtocolInfo.Version, BuildInfo.ModVersion, config.AllowedGameVersions);
        new AuthPacketHandler(versionCheck).RegisterOnServer(server);
        new PlayerPacketHandler().RegisterOnServer(server);
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
