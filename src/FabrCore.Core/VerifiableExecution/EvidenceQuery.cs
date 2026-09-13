using FabrCore.Core.CloudServer;
namespace FabrCore.Core.VerifiableExecution;
public interface IVerifiableExecutionQueryProvider
{
    Task<AdministrationPage<string>> ListTracesAsync(string? agentHandle = null, string? after = null, int limit = 100, CancellationToken cancellationToken = default);
}
