using System.Text.Json;
using FabrCore.Core.Blueprints;
using FabrCore.Surface.CommandCenter;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSwarmBlueprintExpander : IBlueprintExpander
{
    public string ExtensionKey => "swarm";

    public ValueTask<BlueprintExpansion> ExpandAsync(
        BlueprintExpansionContext context,
        JsonElement extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var document = extension.Deserialize<SwarmBlueprintExtension>(SurfaceJson.Options)
                       ?? new SwarmBlueprintExtension();
        var result = new BlueprintExpansion();

        foreach (var definition in document.Squads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.SquadType == SurfaceSquadType.Swarm)
            {
                var swarmDefinition = SurfaceSwarmInterop.ToSwarmDefinition(definition);
                var squad = SurfaceSquadService.BuildSquad(
                    context.PrincipalId,
                    swarmDefinition);
                var runtime = SurfaceSwarmSquadRuntime.Serialize(
                    new SurfaceSwarmSquadRuntime { Squad = squad });
                result.Agents.AddRange(SurfaceSquadService.BuildAgentConfigurations(
                    swarmDefinition,
                    squad,
                    runtime));
            }
            else
            {
                var squad = SurfaceBasicSquadService.BuildSquad(
                    context.PrincipalId,
                    definition);
                var runtime = SurfaceSquadRuntime.Serialize(
                    new SurfaceSquadRuntime { Squad = squad });
                result.Agents.AddRange(SurfaceBasicSquadService.BuildAgentConfigurations(
                    definition,
                    squad,
                    runtime));
            }
        }

        return ValueTask.FromResult(result);
    }
}
