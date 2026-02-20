using Fabr.Core;

namespace Fabr.Sdk
{
    public interface IFabrPlugin
    {
        Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider);
    }
}
