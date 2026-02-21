using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class StatusCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;
    private readonly CliOptions _options;

    public string Name => "status";
    public string Description => "Show connection and silo status";
    public string Usage => "/status";
    public string[] Aliases => [];

    public StatusCommand(IConnectionManager connection, IConsoleRenderer renderer, CliOptions options)
    {
        _connection = connection;
        _renderer = renderer;
        _options = options;
    }

    public Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        _renderer.ShowStatus(_connection.CurrentHandle, _connection.CurrentAgentHandle, _options.Port);
        return Task.CompletedTask;
    }
}
