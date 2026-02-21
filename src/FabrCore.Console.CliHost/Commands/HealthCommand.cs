using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class HealthCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;

    public string Name => "health";
    public string Description => "Show agent health status";
    public string Usage => "/health [handle]";
    public string[] Aliases => [];

    public HealthCommand(IConnectionManager connection, IConsoleRenderer renderer)
    {
        _connection = connection;
        _renderer = renderer;
    }

    public async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var handle = args.Length > 0 ? args[0] : null;

        try
        {
            var health = await _connection.GetHealthAsync(handle, ct);
            _renderer.ShowHealth(health);
        }
        catch (InvalidOperationException ex)
        {
            _renderer.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to get health: {ex.Message}");
        }
    }
}
