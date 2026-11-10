namespace Cyberverse.Server.Commands;

/// <summary>The server console: may run every command.</summary>
public sealed class ConsoleCommandSender : ICommandSender
{
    public string Name => "Console";

    public void Reply(string message) => Console.WriteLine(message);

    public bool HasPermission(string permission) => true;
}
