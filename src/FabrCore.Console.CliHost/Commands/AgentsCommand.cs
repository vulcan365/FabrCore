using FabrCore.Console.CliHost.Services;
using FabrCore.Core;

namespace FabrCore.Console.CliHost.Commands;

public class AgentsCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;

    public string Name => "agents";
    public string Description => "List agents and optionally select one";
    public string Usage => "/agents [active|deactivated]";
    public string[] Aliases => ["ls"];

    public AgentsCommand(IConnectionManager connection, IConsoleRenderer renderer)
    {
        _connection = connection;
        _renderer = renderer;
    }

    public async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var statusFilter = args.Length > 0 ? args[0] : null;

        try
        {
            var result = await _connection.GetAgentsAsync(statusFilter, ct);

            // Filter out Client entities - only show actual agents
            var agents = result.Agents.Where(a => a.EntityType == EntityType.Agent).ToList();

            if (agents.Count == 0)
            {
                _renderer.ShowWarning("No agents found. Use /create to create one.");
                return;
            }

            _renderer.ShowAgentTable(agents);

            // Interactive selection
            var handles = agents.Select(a => a.Handle).ToList();
            var selected = _renderer.ShowAgentSelectionPrompt(handles);

            if (selected != null)
            {
                _connection.ConnectToAgent(selected);
                var agent = agents.First(a => a.Handle == selected);
                _renderer.ShowSuccess($"Connected to {selected} ({agent.AgentType})");
            }
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to list agents: {ex.Message}");
        }
    }
}
