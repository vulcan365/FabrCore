using System.Text.Json;
using FabrCore.Core;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSwarmSquadRuntime
{
    public SurfaceSwarmSquad Squad { get; set; } = new();

    public SurfaceSwarmSquadAgent? FindAgent(string name)
        => Squad.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Handle, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ShortHandle(agent.Handle), name, StringComparison.OrdinalIgnoreCase));

    public static SurfaceSwarmSquadRuntime FromConfiguration(AgentConfiguration config, string fallbackHandle)
    {
        if (config.Args.TryGetValue(SurfaceSwarmArgs.SquadDefinition, out var json)
            && !string.IsNullOrWhiteSpace(json))
        {
            var runtime = JsonSerializer.Deserialize<SurfaceSwarmSquadRuntime>(json, SurfaceJson.Options);
            if (runtime is not null)
            {
                return runtime;
            }
        }

        var (principal, alias) = HandleUtilities.ParseHandle(fallbackHandle);
        return new SurfaceSwarmSquadRuntime
        {
            Squad = new SurfaceSwarmSquad
            {
                Name = alias,
                Slug = alias,
                PrincipalHandle = principal,
                OrchestratorHandle = fallbackHandle,
                PlannerHandle = $"{fallbackHandle}-planner",
                SupervisorHandle = $"{fallbackHandle}-supervisor",
                VerifierHandle = $"{fallbackHandle}-verifier"
            }
        };
    }

    public static string Serialize(SurfaceSwarmSquadRuntime runtime)
        => JsonSerializer.Serialize(runtime, SurfaceJson.Options);

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
