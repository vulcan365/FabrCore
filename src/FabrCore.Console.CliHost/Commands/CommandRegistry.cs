namespace FabrCore.Console.CliHost.Commands;

public class CommandRegistry
{
    private readonly Dictionary<string, ICliCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICliCommand> _allCommands;

    public CommandRegistry(IEnumerable<ICliCommand> commands)
    {
        _allCommands = commands.ToList();

        foreach (var command in _allCommands)
        {
            _commands[command.Name] = command;
            foreach (var alias in command.Aliases)
            {
                _commands[alias] = command;
            }
        }
    }

    public ICliCommand? GetCommand(string name)
    {
        _commands.TryGetValue(name, out var command);
        return command;
    }

    public IReadOnlyList<ICliCommand> GetAllCommands() => _allCommands;
}
