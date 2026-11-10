using System.Text.RegularExpressions;
using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.PacketHandling;

/// <summary>
/// Decides whether a client's versions may join: the protocol version (a hash of protocol.yaml) must match exactly, the
/// mod version must be the same release, and the game version must be one the server allows.
/// </summary>
public partial class LoginVersionCheck(uint protocolVersion, string modVersion, IReadOnlyList<string> allowedGameVersions)
{
    public readonly record struct Result(EAuthResult AuthResult, string Message, string LogReason)
    {
        public bool Ok => AuthResult == EAuthResult.Ok;
    }

    public Result Check(uint clientProtocol, string clientMod, string clientGame)
    {
        var serverRelease = Release(modVersion);
        var clientRelease = Release(clientMod);
        if (clientProtocol != protocolVersion || clientRelease != serverRelease)
        {
            return new Result(EAuthResult.VersionMismatch,
                $"This server runs Cyberverse {modVersion}, you have {Display(clientMod)}. " +
                $"Install Cyberverse {serverRelease} to join.",
                $"mod {Display(clientMod)} protocol 0x{clientProtocol:X8}, the server has {modVersion} protocol 0x{protocolVersion:X8}");
        }

        if (allowedGameVersions.Count > 0 && !allowedGameVersions.Any(allowed => GameVersionMatches(allowed, clientGame)))
        {
            var allowedText = string.Join(" or ", allowedGameVersions);
            return new Result(EAuthResult.VersionMismatch,
                $"This server needs Cyberpunk 2077 version {allowedText}, you have {Display(clientGame)}. " +
                "Update or downgrade the game to join.",
                $"game {Display(clientGame)}, the server allows {allowedText}");
        }

        return new Result(EAuthResult.Ok, "", "");
    }

    /// <summary>
    /// The release a git describe string belongs to: "v0.1.0-12-gaba70a7-dirty" and "v0.1.0" are both v0.1.0. Builds
    /// that aren't on a release tag (a plain commit hash, or "unknown") are all "dev", so local builds can always meet.
    /// </summary>
    public static string Release(string version)
    {
        var match = ReleaseTag().Match(version);
        return match.Success ? match.Groups[1].Value : "dev";
    }

    /// <summary>"2.31" allows 2.31 and every 2.31.x; "2.31.0" only exactly that.</summary>
    public static bool GameVersionMatches(string allowed, string game) =>
        game == allowed || game.StartsWith(allowed + ".", StringComparison.Ordinal);

    private static string Display(string version) => version.Length == 0 ? "an unknown version" : version;

    [GeneratedRegex(@"^(v\d+\.\d+\.\d+)(?:-\d+-g[0-9a-f]+)?(?:-dirty)?$")]
    private static partial Regex ReleaseTag();
}
