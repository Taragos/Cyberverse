namespace Cyberverse.Server.Commands;

/// <summary>Runs a command. Reply through <see cref="CommandContext.Sender"/>.</summary>
public delegate void CommandHandler(CommandContext context);

/// <param name="Name">Lower case, without a slash, e.g. "kick".</param>
/// <param name="Description">One line for help.</param>
/// <param name="Handler">Runs the command.</param>
/// <param name="Usage">Arguments for help, e.g. "&lt;player&gt; [reason]".</param>
/// <param name="Permission">Checked with <see cref="ICommandSender.HasPermission"/>; null lets everyone run it.</param>
public sealed record CommandDefinition(string Name, string Description, CommandHandler Handler,
    string Usage = "", string? Permission = null);

/// <param name="Sender">Who ran it.</param>
/// <param name="Name">The command as typed, lower case.</param>
/// <param name="Args">The words after the name; "double quotes" group words.</param>
/// <param name="ArgumentText">Everything after the name as typed, e.g. for /me.</param>
public sealed record CommandContext(ICommandSender Sender, string Name, IReadOnlyList<string> Args, string ArgumentText);

/// <summary>Who runs a command: the server console, or later a player in chat.</summary>
public interface ICommandSender
{
    string Name { get; }

    void Reply(string message);

    bool HasPermission(string permission);
}

public enum CommandResult
{
    Ran,
    Unknown,
    NotPermitted,
    /// <summary>The handler threw; it was logged and the sender told.</summary>
    Failed,
}

/// <summary>Commands for the console and chat. Names are case-insensitive and unique.</summary>
public interface ICommandRegistry
{
    /// <summary>Dispose the result to remove the command. Throws when the name is taken.</summary>
    IDisposable Register(CommandDefinition command);

    IReadOnlyCollection<CommandDefinition> Commands { get; }

    /// <summary>Runs a line like "kick Bob being rude" (a leading slash is ignored).</summary>
    CommandResult Execute(ICommandSender sender, string line);
}
