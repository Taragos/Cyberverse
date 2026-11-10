using Cyberverse.Server.Config;
using NLog;
using NLog.Targets;

namespace Cyberverse.Server.Tests;

// Changes NLog's global configuration, so it must not run beside other tests that do.
[Collection(nameof(LoggingTests))]
[CollectionDefinition(nameof(LoggingTests), DisableParallelization = true)]
public sealed class LoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cyberverse-logs-" + Guid.NewGuid());

    public void Dispose()
    {
        LogManager.Configuration = null; // closes the files, so Windows lets us delete them
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Read(string path)
    {
        LogManager.Flush();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void WritesTheRunFileAndLatestAtTheConfiguredLevel()
    {
        var terminal = new MemoryTarget("terminal") { Layout = "${logger}|${message}" };
        var file = Logging.Configure(LogLevel.Info, _directory, new DateTime(2026, 11, 10, 7, 51, 9), out _, terminal);

        LogManager.GetLogger("Test").Info("player joined");
        LogManager.GetLogger("Test").Debug("too detailed");
        LogManager.GetLogger(Logging.ConsoleLoggerName).Info("> plugins");

        Assert.Equal(Path.Combine(Path.GetFullPath(_directory), "server-2026-11-10_07-51-09.log"), file);
        foreach (var path in new[] { file!, Path.Combine(_directory, Logging.LatestFileName) })
        {
            var text = Read(path);
            Assert.Contains("|INFO|Test|player joined", text);
            Assert.Contains("|INFO|Console|> plugins", text);
            Assert.DoesNotContain("too detailed", text);
        }

        // The terminal shows typed commands and replies already; only the files get them from the logger.
        Assert.Equal(["Test|player joined"], terminal.Logs);
    }

    [Fact]
    public void LatestStartsEmptyForEachRun()
    {
        Logging.Configure(LogLevel.Info, _directory, new DateTime(2026, 11, 10, 7, 0, 0), out _, new MemoryTarget());
        LogManager.GetLogger("Test").Info("first run");
        LogManager.Flush();

        var second = Logging.Configure(LogLevel.Info, _directory, new DateTime(2026, 11, 10, 8, 0, 0), out _, new MemoryTarget());
        LogManager.GetLogger("Test").Info("second run");

        var latest = Read(Path.Combine(_directory, Logging.LatestFileName));
        Assert.DoesNotContain("first run", latest);
        Assert.Contains("second run", latest);
        Assert.Contains("first run", Read(Path.Combine(_directory, "server-2026-11-10_07-00-00.log")));
        Assert.Contains("second run", Read(second!));
    }

    [Fact]
    public void FallsBackToTheConsoleWhenTheFolderCantBeCreated()
    {
        // A file where the folder should be: creating the folder fails on every OS, also as root.
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "logs");
        File.WriteAllText(blocked, "");
        var terminal = new MemoryTarget("terminal") { Layout = "${message}" };

        var file = Logging.Configure(LogLevel.Info, blocked, DateTime.Now, out var error, terminal);
        LogManager.GetLogger("Test").Info("still logged");

        Assert.Null(file);
        Assert.Contains(blocked, error);
        Assert.Equal(["still logged"], terminal.Logs);
    }

    [Fact]
    public void KeepsOnlyTheNewestRuns()
    {
        Directory.CreateDirectory(_directory);
        var names = Enumerable.Range(1, 5).Select(day => $"server-2026-11-0{day}_12-00-00.log").ToList();
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_directory, name), "");
        }

        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "");

        Logging.DeleteOldRuns(_directory, 2);

        Assert.Equal(["notes.txt", names[3], names[4]], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }
}

public class ServerConfigLoggingTests
{
    private static ServerConfig Load(string json, Func<string, string?>? env = null)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        try
        {
            return ServerConfig.Load(path, env ?? (_ => null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsTheLevelAndFolderFromTheFileAndTheEnvironment()
    {
        var config = Load("""{ "logLevel": "debug", "logDirectory": "/srv/logs" }""");
        Assert.Equal(LogLevel.Debug, config.ResolveLogLevel());
        Assert.Equal("/srv/logs", config.LogDirectory);

        var fromEnv = Load("""{ "logLevel": "debug" }""",
            name => name switch { "CYBERVERSE_LOG_LEVEL" => "Warn", "CYBERVERSE_LOG_DIR" => "elsewhere", _ => null });
        Assert.Equal(LogLevel.Warn, fromEnv.ResolveLogLevel());
        Assert.Equal("elsewhere", fromEnv.LogDirectory);
    }

    [Fact]
    public void DefaultsToTheBuildsLevelAndLogsFolder()
    {
        var config = Load("{}");
#if DEBUG
        Assert.Equal(LogLevel.Trace, config.ResolveLogLevel());
#else
        Assert.Equal(LogLevel.Info, config.ResolveLogLevel());
#endif
        Assert.Equal("logs", config.LogDirectory);
    }

    [Fact]
    public void RejectsAnUnknownLevelAtLoad()
    {
        var error = Assert.Throws<InvalidDataException>(() => Load("""{ "logLevel": "verbose" }"""));
        Assert.Contains("Trace", error.Message);
    }
}
