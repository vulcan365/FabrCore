using FabrCore.Core.Skills;
using Orleans;

namespace FabrCore.Core.Interfaces;

/// <summary>Serializes a principal's skill-catalog index mutations across silos.</summary>
public interface IFabrCoreSkillCatalogGrain : IGrainWithStringKey
{
    Task<List<FabrCoreSkillCatalogEntry>> ListAsync();
    Task UpsertAsync(FabrCoreSkillCatalogEntry entry);
    Task<bool> RemoveAsync(string name, string version);
}
