using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class ConnectCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;

    public string Name => "connect";
    public string Description => "Connect to an agent by handle";
    public string Usage => "/connect <handle>";
    public string[] Aliases => ["c"];

    public ConnectCommand(IConnectionManager connection, IConsoleRenderer renderer)
    {
        _connection = connection;
        _renderer = renderer;
    }

    public Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            _renderer.ShowError("Usage: /connect <handle>");
            return Task.CompletedTask;
        }

        var handle = args[0];
        _connection.ConnectToAgent(handle);
        _renderer.ShowSuccess($"Connected to {handle}");

        return Task.CompletedTask;
    }
}
