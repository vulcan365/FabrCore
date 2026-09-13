using Orleans;
using Orleans.Concurrency;
namespace FabrCore.Core.Interfaces;
internal interface IAdministrationOperationGrain : IGrainWithStringKey
{
    Task<string> StartAsync(string request);
    [AlwaysInterleave] Task<string> ReadAsync();
}
