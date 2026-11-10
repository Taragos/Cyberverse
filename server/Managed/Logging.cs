using NLog;
using NLog.Config;
using NLog.Targets;

namespace Cyberverse.Server;

/// <summary>
/// The console, plus one log file per run (logs/server-&lt;start time&gt;.log) and logs/latest.log with the same lines.
/// Console command replies and typed commands (logger <see cref="ConsoleLoggerName"/>) go only to the files, since the
/// terminal shows them already.
/// </summary>
public static class Logging
{
    /// <summary>How many run files are kept, the current one included.</summary>
    public const int KeptRuns = 20;

    public const string LatestFileName = "latest.log";

    /// <summary>Logger for console input and command replies.</summary>
    public const string ConsoleLoggerName = "Console";

    private const string RunFilePattern = "server-*.log";

    /// <summary>Sets up NLog and returns the full path of this run's log file.</summary>
    /// <param name="console">The terminal target; null for the colored console.</param>
    public static string Configure(LogLevel level, string directory, DateTime startedAt, Target? console = null)
    {
        Directory.CreateDirectory(directory);
        DeleteOldRuns(directory, KeptRuns - 1);

        var runFile = Path.GetFullPath(Path.Combine(directory, $"server-{startedAt:yyyy-MM-dd_HH-mm-ss}.log"));
        var layout = "${longdate}|${level:uppercase=true}|${logger}|${message:withexception=true}";
        var files = new Target[]
        {
            new FileTarget("run") { FileName = runFile, Layout = layout },
            new FileTarget("latest")
            {
                FileName = Path.GetFullPath(Path.Combine(directory, LatestFileName)),
                Layout = layout,
                DeleteOldFileOnStartup = true,
            },
        };

        var config = new LoggingConfiguration();
        // First and final: console input and replies stop here, in the files.
        var consoleRule = new LoggingRule(ConsoleLoggerName, LogLevel.Trace, LogLevel.Fatal, files[0]) { Final = true };
        consoleRule.Targets.Add(files[1]);
        config.LoggingRules.Add(consoleRule);

        var targets = files.ToList();
        targets.Add(console ?? new ColoredConsoleTarget("console")
        {
            EnableAnsiOutput = true, // Maybe this improves the Open Game Panel(?)
            DetectOutputRedirected = true, // Disables colors when a file is detected
        });

        foreach (var target in targets)
        {
            config.AddRule(level, LogLevel.Fatal, target);
        }

        LogManager.Configuration = config;
        return runFile;
    }

    /// <summary>Deletes the oldest run files so that at most keep remain. Files in use are skipped.</summary>
    public static void DeleteOldRuns(string directory, int keep)
    {
        // The timestamp in the name sorts by time.
        var old = Directory.GetFiles(directory, RunFilePattern)
            .Order(StringComparer.Ordinal)
            .Reverse()
            .Skip(Math.Max(keep, 0));
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // another server still writes to it
            }
        }
    }
}
