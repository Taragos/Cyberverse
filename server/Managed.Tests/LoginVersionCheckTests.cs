using Cyberverse.Server.Config;
using Cyberverse.Server.PacketHandling;
using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Tests;

public class LoginVersionCheckTests
{
    private const uint Protocol = 0x74BF4AE9;

    private static LoginVersionCheck Server(string mod = "v0.3.1", params string[] games) => new(Protocol, mod, games);

    [Theory]
    [InlineData("v0.3.1", "v0.3.1")]
    [InlineData("v0.3.1", "v0.3.1-4-gabc1234")]
    [InlineData("v0.3.1-2-gdef5678-dirty", "v0.3.1-dirty")]
    [InlineData("aba70a7-dirty", "1421948")] // untagged dev builds meet
    [InlineData("unknown", "aba70a7")]
    public void AcceptsTheSameRelease(string serverMod, string clientMod)
    {
        var result = Server(serverMod).Check(Protocol, clientMod, "2.31.0");
        Assert.Equal(EAuthResult.Ok, result.AuthResult);
        Assert.Equal("", result.Message);
    }

    [Theory]
    [InlineData("v0.3.1", "v0.3.0")]
    [InlineData("v0.3.1", "v0.3.0-12-gabc1234")]
    [InlineData("v0.3.1", "aba70a7")]
    [InlineData("v0.3.1", "")]
    public void RejectsAnotherRelease(string serverMod, string clientMod)
    {
        var result = Server(serverMod).Check(Protocol, clientMod, "2.31.0");
        Assert.Equal(EAuthResult.VersionMismatch, result.AuthResult);
        Assert.Contains($"This server runs Cyberverse {serverMod}", result.Message);
        Assert.Contains("Install Cyberverse v0.3.1 to join", result.Message);
    }

    [Fact]
    public void RejectsAnotherProtocolEvenWithTheSameRelease()
    {
        var result = Server().Check(Protocol + 1, "v0.3.1", "2.31.0");
        Assert.Equal(EAuthResult.VersionMismatch, result.AuthResult);
        Assert.Contains("protocol 0x74BF4AEA", result.LogReason);
    }

    [Fact]
    public void AnyGameVersionWhenNoneIsConfigured()
    {
        Assert.True(Server().Check(Protocol, "v0.3.1", "1.6.0").Ok);
    }

    [Theory]
    [InlineData("2.31", "2.31.0", true)]
    [InlineData("2.31", "2.31.12", true)]
    [InlineData("2.31", "2.31", true)]
    [InlineData("2.3", "2.31.0", false)]
    [InlineData("2.31.0", "2.31.1", false)]
    [InlineData("2.31.0", "", false)]
    public void GameVersionPrefixes(string allowed, string game, bool ok)
    {
        var result = Server("v0.3.1", allowed).Check(Protocol, "v0.3.1", game);
        Assert.Equal(ok, result.Ok);
        if (!ok)
        {
            Assert.Contains($"needs Cyberpunk 2077 version {allowed}", result.Message);
        }
    }

    [Fact]
    public void ListsEveryAllowedGameVersion()
    {
        var result = Server("v0.3.1", "2.31", "2.3.1").Check(Protocol, "v0.3.1", "2.2.0");
        Assert.Contains("version 2.31 or 2.3.1, you have 2.2.0", result.Message);
    }

    [Fact]
    public void MessagesFitTheFrozenPacket()
    {
        var longVersion = new string('x', 64);
        var result = Server("v0.3.1", Enumerable.Repeat("2.31.0", 20).ToArray()).Check(Protocol, longVersion, longVersion);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Message) <= 1024);
    }
}

public class ServerConfigTests
{
    [Fact]
    public void DefaultsWithoutAFile()
    {
        var config = ServerConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => null);
        Assert.Empty(config.AllowedGameVersions);
    }

    [Fact]
    public void ReadsTheFileAndLetsTheEnvironmentOverride()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """
            {
              // comments are allowed
              "allowedGameVersions": ["2.31"],
            }
            """);
        try
        {
            Assert.Equal(["2.31"], ServerConfig.Load(path, _ => null).AllowedGameVersions);
            var fromEnv = ServerConfig.Load(path, name => name == "CYBERVERSE_ALLOWED_GAME_VERSIONS" ? "2.31.0, 2.3.1" : null);
            Assert.Equal(["2.31.0", "2.3.1"], fromEnv.AllowedGameVersions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsUnknownKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "allowedGameVersion": ["2.31"] }""");
        try
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => ServerConfig.Load(path, _ => null));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
