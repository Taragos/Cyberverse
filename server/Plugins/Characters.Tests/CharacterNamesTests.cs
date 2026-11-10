namespace Cyberverse.Plugin.Characters.Tests;

public class CharacterNamesTests
{
    [Theory]
    [InlineData("Alex", "Example")]
    [InlineData("Jean-Luc", "O'Neil")]
    [InlineData("Zoë", "Åberg")]
    [InlineData("Al", "Abcdefghijklmnop")] // 2 and 16 letters
    public void ValidNames(string first, string last)
    {
        Assert.True(CharacterNames.TryNormalize(first, last, out _, out _, out var error), error);
    }

    [Theory]
    [InlineData("A", "Example")]
    [InlineData("Alex", "Abcdefghijklmnopq")] // 17 letters
    [InlineData("Al3x", "Example")]
    [InlineData("-Alex", "Example")]
    [InlineData("Alex Bob", "Example")]
    [InlineData("Alex", "")]
    public void InvalidNames(string first, string last)
    {
        Assert.False(CharacterNames.TryNormalize(first, last, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SpacesAroundAreTrimmed()
    {
        Assert.True(CharacterNames.TryNormalize("  Alex ", " Example", out var first, out var last, out _));
        Assert.Equal(("Alex", "Example"), (first, last));
    }

    [Fact]
    public void CaseDoesNotMakeANameDifferent()
    {
        Assert.Equal(CharacterNames.Key("Alex", "Example"), CharacterNames.Key("aLEX", "EXAMPLE"));
    }
}
