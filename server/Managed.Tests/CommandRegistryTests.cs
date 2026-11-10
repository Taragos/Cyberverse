using Cyberverse.Server.Commands;

namespace Cyberverse.Server.Tests;

public class CommandRegistryTests
{
    private sealed class Sender(params string[] permissions) : ICommandSender
    {
        public readonly List<string> Replies = [];
        public string Name => "test";
        public void Reply(string message) => Replies.Add(message);
        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    [Fact]
    public void RunsACommandWithItsArguments()
    {
        var registry = new CommandRegistry();
        CommandContext? seen = null;
        registry.Register(new CommandDefinition("kick", "Kicks a player", ctx => seen = ctx));

        var result = registry.Execute(new Sender(), "/KICK  Bob \"being rude\" now ");

        Assert.Equal(CommandResult.Ran, result);
        Assert.Equal("kick", seen!.Name);
        Assert.Equal(["Bob", "being rude", "now"], seen.Args);
        Assert.Equal("Bob \"being rude\" now", seen.ArgumentText);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a b", new[] { "a", "b" })]
    [InlineData("\"\" b", new[] { "", "b" })]
    [InlineData("a\"b c\"d", new[] { "ab cd" })]
    [InlineData("\"unclosed quote", new[] { "unclosed quote" })]
    public void SplitsArguments(string text, string[] expected)
    {
        Assert.Equal(expected, CommandRegistry.SplitArguments(text));
    }

    [Fact]
    public void ChecksThePermission()
    {
        var registry = new CommandRegistry();
        var ran = false;
        registry.Register(new CommandDefinition("ban", "", _ => ran = true, Permission: "admin.ban"));

        var player = new Sender();
        Assert.Equal(CommandResult.NotPermitted, registry.Execute(player, "ban Bob"));
        Assert.False(ran);
        Assert.Single(player.Replies);

        Assert.Equal(CommandResult.Ran, registry.Execute(new Sender("admin.ban"), "ban Bob"));
        Assert.True(ran);
    }

    [Fact]
    public void ReportsUnknownAndFailingCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition("broken", "", _ => throw new InvalidOperationException("boom")));
        var sender = new Sender();

        Assert.Equal(CommandResult.Unknown, registry.Execute(sender, "nope"));
        Assert.Equal(CommandResult.Failed, registry.Execute(sender, "broken"));
        Assert.Contains("boom", sender.Replies[1]);
    }

    [Fact]
    public void NamesAreUniqueUntilUnregistered()
    {
        var registry = new CommandRegistry();
        var registration = registry.Register(new CommandDefinition("me", "", _ => { }));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new CommandDefinition("ME", "", _ => { })));
        registration.Dispose();
        registry.Register(new CommandDefinition("me", "", _ => { }));
        Assert.Throws<ArgumentException>(() => registry.Register(new CommandDefinition("two words", "", _ => { })));
    }
}
