using System.Text;
using NLog;

namespace Cyberverse.Server.Commands;

public sealed class CommandRegistry : ICommandRegistry
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CommandDefinition> Commands => _commands.Values;

    public IDisposable Register(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Any(char.IsWhiteSpace) || command.Name.StartsWith('/'))
        {
            throw new ArgumentException($"Invalid command name \"{command.Name}\"", nameof(command));
        }

        if (!_commands.TryAdd(command.Name, command))
        {
            throw new InvalidOperationException($"The command \"{command.Name}\" is already registered");
        }

        return new Disposable(() => _commands.Remove(command.Name));
    }

    public CommandResult Execute(ICommandSender sender, string line)
    {
        var text = line.Trim().TrimStart('/');
        var space = text.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var argumentText = space < 0 ? "" : text[(space + 1)..].Trim();

        if (name.Length == 0 || !_commands.TryGetValue(name, out var command))
        {
            sender.Reply($"Unknown command \"{name}\". Try \"help\".");
            return CommandResult.Unknown;
        }

        if (command.Permission != null && !sender.HasPermission(command.Permission))
        {
            sender.Reply($"You may not use \"{name}\".");
            return CommandResult.NotPermitted;
        }

        try
        {
            command.Handler(new CommandContext(sender, name, SplitArguments(argumentText), argumentText));
            return CommandResult.Ran;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Command \"{line}\" from {sender.Name} threw");
            sender.Reply($"\"{name}\" failed: {e.Message}");
            return CommandResult.Failed;
        }
    }

    /// <summary>Splits on whitespace; "double quotes" keep words together. An unclosed quote runs to the end.</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasArgument = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasArgument)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (hasArgument)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
