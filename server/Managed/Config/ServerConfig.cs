using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyberverse.Server.Config;

/// <summary>
/// Settings read at startup from server.json in the working directory (optional; every key has a default). Environment
/// variables override single keys, which is easier with Docker.
/// </summary>
public record ServerConfig
{
    public const string FileName = "server.json";

    /// <summary>
    /// Game versions players may join with, as RED4ext reports them (e.g. "2.31.0"). "2.31" allows every 2.31.x. Empty
    /// allows any game version; it is still logged. Environment: CYBERVERSE_ALLOWED_GAME_VERSIONS=2.31,2.3.1
    /// </summary>
    public string[] AllowedGameVersions { get; init; } = [];

    /// <summary>
    /// Lowest level written to the console and the log file: Trace, Debug, Info, Warn, Error or Fatal. Null means Trace
    /// in Debug builds and Info in Release builds. Environment: CYBERVERSE_LOG_LEVEL=Debug
    /// </summary>
    public string? LogLevel { get; init; }

    /// <summary>Folder for the log files, relative to the working directory. Environment: CYBERVERSE_LOG_DIR=/var/log/cyberverse</summary>
    public string LogDirectory { get; init; } = "logs";

    public static ServerConfig Load(string path, Func<string, string?> getEnvironmentVariable)
    {
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions) ?? new ServerConfig()
            : new ServerConfig();

        if (getEnvironmentVariable("CYBERVERSE_ALLOWED_GAME_VERSIONS") is { } games)
        {
            config = config with
            {
                AllowedGameVersions = games.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };
        }

        if (getEnvironmentVariable("CYBERVERSE_LOG_LEVEL") is { Length: > 0 } level)
        {
            config = config with { LogLevel = level };
        }

        if (getEnvironmentVariable("CYBERVERSE_LOG_DIR") is { Length: > 0 } directory)
        {
            config = config with { LogDirectory = directory };
        }

        // Fail at startup rather than on the first log line.
        _ = config.ResolveLogLevel();
        return config;
    }

    /// <summary>The configured level, or the build's default. Throws for an unknown name.</summary>
    public NLog.LogLevel ResolveLogLevel()
    {
        if (LogLevel == null)
        {
#if DEBUG
            return NLog.LogLevel.Trace;
#else
            return NLog.LogLevel.Info;
#endif
        }

        var level = NLog.LogLevel.AllLoggingLevels.FirstOrDefault(x => x.Name.Equals(LogLevel, StringComparison.OrdinalIgnoreCase));
        return level ?? throw new InvalidDataException(
            $"Unknown log level \"{LogLevel}\"; use one of {string.Join(", ", NLog.LogLevel.AllLoggingLevels.Select(x => x.Name))}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
