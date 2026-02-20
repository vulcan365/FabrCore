using FabrCore.Core;

namespace FabrCore.Sdk
{
    public interface IFabrCorePlugin
    {
        Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider);
    }
}
