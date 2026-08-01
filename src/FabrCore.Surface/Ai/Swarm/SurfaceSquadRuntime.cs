using System.Text.Json;
using FabrCore.Core;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSquadRuntime
{
    public SurfaceSquad Squad { get; set; } = new();

    public SurfaceSquadAgent? FindAgent(string name)
        => Squad.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Handle, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ShortHandle(agent.Handle), name, StringComparison.OrdinalIgnoreCase));

    public static SurfaceSquadRuntime FromConfiguration(AgentConfiguration config, string fallbackHandle)
    {
        if (config.Args.TryGetValue(SurfaceSquadArgs.SquadDefinition, out var json)
            && !string.IsNullOrWhiteSpace(json))
        {
            var runtime = JsonSerializer.Deserialize<SurfaceSquadRuntime>(json, SurfaceJson.Options);
            if (runtime is not null)
            {
                return runtime;
            }
        }

        var (principal, alias) = HandleUtilities.ParseHandle(fallbackHandle);
        return new SurfaceSquadRuntime
        {
            Squad = new SurfaceSquad
            {
                Name = alias,
                Slug = alias,
                PrincipalHandle = principal,
                OrchestratorHandle = fallbackHandle,
                PlannerHandle = $"{fallbackHandle}-planner"
            }
        };
    }

    public static string Serialize(SurfaceSquadRuntime runtime)
        => JsonSerializer.Serialize(runtime, SurfaceJson.Options);

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
