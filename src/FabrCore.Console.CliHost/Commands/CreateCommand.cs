using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class CreateCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;

    public string Name => "create";
    public string Description => "Create a new agent and auto-connect";
    public string Usage => "/create <agentType> [handle]";
    public string[] Aliases => ["new"];

    public CreateCommand(IConnectionManager connection, IConsoleRenderer renderer)
    {
        _connection = connection;
        _renderer = renderer;
    }

    public async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            _renderer.ShowError("Usage: /create <agentType> [handle]");
            return;
        }

        var agentType = args[0];
        var handle = args.Length > 1 ? args[1] : null;

        try
        {
            _renderer.ShowInfo($"Creating agent of type '{agentType}'...");
            var health = await _connection.CreateAgentAsync(agentType, handle, ct);
            _renderer.ShowHealth(health);
            _renderer.ShowSuccess($"Created and connected to {health.Handle}");
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to create agent: {ex.Message}");
        }
    }
}
