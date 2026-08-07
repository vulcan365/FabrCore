using System.Text.Json;
using FabrCore.Core.Blueprints;

namespace FabrCore.Surface.Ai.Squads;

public sealed class SurfaceSquadBlueprintExpander : IBlueprintExpander
{
    public string ExtensionKey => "squads";

    public ValueTask<BlueprintExpansion> ExpandAsync(
        BlueprintExpansionContext context,
        JsonElement extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var squads = extension.Deserialize<List<SurfaceSquadDefinition>>(SurfaceJson.Options) ?? [];
        var result = new BlueprintExpansion();

        foreach (var definition in squads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var squad = SurfaceSquadService.BuildSquad(
                context.PrincipalId,
                definition);
            var runtime = SurfaceSquadRuntime.Serialize(
                new SurfaceSquadRuntime { Squad = squad });
            result.Agents.AddRange(SurfaceSquadService.BuildAgentConfigurations(
                definition,
                squad,
                runtime));
        }

        return ValueTask.FromResult(result);
    }
}
