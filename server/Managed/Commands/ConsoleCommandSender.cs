using NLog;

namespace Cyberverse.Server.Commands;

/// <summary>The server console: may run every command. Replies go to the terminal and the log file.</summary>
public sealed class ConsoleCommandSender : ICommandSender
{
    private static readonly Logger Logger = LogManager.GetLogger(Logging.ConsoleLoggerName);

    public string Name => "Console";

    public void Reply(string message)
    {
        Console.WriteLine(message);
        Logger.Info(message);
    }

    public bool HasPermission(string permission) => true;
}
