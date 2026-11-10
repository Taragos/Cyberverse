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

        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
