using Orleans;
namespace FabrCore.Core.Interfaces;
internal interface IBlueprintAdministrationGrain : IGrainWithStringKey
{
    Task<string> ExecuteAsync(string operation, string name, string body, string? revision);
}
