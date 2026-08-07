using FabrCore.Core.Interfaces;
using FabrCore.Core.Skills;
using FabrCore.Host.Configuration;
using Orleans.Runtime;

namespace FabrCore.Host.Grains;

public sealed class FabrCoreSkillCatalogGrain(
    [PersistentState("skillCatalog", FabrCoreOrleansConstants.StorageProviderName)]
    IPersistentState<Dictionary<string, FabrCoreSkillCatalogEntry>> state)
    : Grain, IFabrCoreSkillCatalogGrain
{
    public Task<List<FabrCoreSkillCatalogEntry>> ListAsync() => Task.FromResult(state.State.Values
        .OrderBy(entry => entry.Name, StringComparer.Ordinal)
        .ThenBy(entry => entry.Version, StringComparer.Ordinal)
        .ToList());

    public async Task UpsertAsync(FabrCoreSkillCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        state.State[Key(entry.Name, entry.Version)] = entry;
        await state.WriteStateAsync();
    }

    public async Task<bool> RemoveAsync(string name, string version)
    {
        if (!state.State.Remove(Key(name, version)))
        {
            return false;
        }

        await state.WriteStateAsync();
        return true;
    }

    private static string Key(string name, string version) => $"{name}@{version}";
}

