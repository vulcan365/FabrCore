using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class HelpCommand : ICliCommand
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConsoleRenderer _renderer;

    public string Name => "help";
    public string Description => "List all available commands";
    public string Usage => "/help";
    public string[] Aliases => ["h", "?"];

    public HelpCommand(IServiceProvider serviceProvider, IConsoleRenderer renderer)
    {
        _serviceProvider = serviceProvider;
        _renderer = renderer;
    }

    public Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var registry = _serviceProvider.GetRequiredService<CommandRegistry>();
        var commands = registry.GetAllCommands()
            .Select(c => (c.Name, c.Aliases, c.Description, c.Usage));

        _renderer.ShowHelp(commands);
        return Task.CompletedTask;
    }
}
