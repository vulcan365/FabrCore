using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Core.Interfaces;
using Orleans;
namespace FabrCore.Host.Services;
internal sealed class FabrCoreBlueprintService(IClusterClient cluster) : IFabrCoreBlueprintService
{
    private IBlueprintAdministrationGrain For(string principal) => cluster.GetGrain<IBlueprintAdministrationGrain>(principal);
    public async Task<FabrCoreBlueprintApplyResult> ApplyAsync(string principalId, FabrCoreBlueprint blueprint, HealthDetailLevel detailLevel = HealthDetailLevel.Basic, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<FabrCoreBlueprintApplyResult>(await For(principalId).ExecuteAsync("apply", blueprint.Name ?? "", JsonSerializer.Serialize(blueprint, JsonSerializerOptions.Web), ((int)detailLevel).ToString(System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken), JsonSerializerOptions.Web)!;
    public async Task<FabrCoreBlueprint?> GetAsync(string principalId, string name, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<FabrCoreBlueprint>(await For(principalId).ExecuteAsync("get", name, "{}", null).WaitAsync(cancellationToken), JsonSerializerOptions.Web);
    public async Task<IReadOnlyList<string>> ListAsync(string principalId, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<List<string>>(await For(principalId).ExecuteAsync("list", "", "{}", null).WaitAsync(cancellationToken), JsonSerializerOptions.Web)!;
    public async Task SaveAsync(string principalId, FabrCoreBlueprint blueprint, CancellationToken cancellationToken = default)
        => await For(principalId).ExecuteAsync("save", blueprint.Name ?? "", JsonSerializer.Serialize(blueprint, JsonSerializerOptions.Web), null).WaitAsync(cancellationToken);
    public async Task<bool> DeleteAsync(string principalId, string name, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<bool>(await For(principalId).ExecuteAsync("delete", name, "{}", null).WaitAsync(cancellationToken));
}
