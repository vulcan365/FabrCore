using System.Text.Json;
using FabrCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Sdk;

public abstract partial class FabrCoreAgentProxy
{
    /// <summary>Optional, agent-bound authenticated connections for trusted agent/plugin code.</summary>
    protected IAgentConnections Connections => new AgentConnections(this);

    private sealed class AgentConnections(FabrCoreAgentProxy proxy) : IAgentConnections
    {
        private (IConnectionService Service, ConnectionBinding Binding) Resolve(string name)
        {
            if (AdminDiagnosticContext.Current.Value is not null)
                throw new ConnectionException("access-denied", "Diagnostic turns cannot access production connections.");
            var service = proxy.serviceProvider.GetService<IConnectionService>()
                ?? throw new ConnectionException("feature-disabled", "Authenticated connections are not enabled.");
            var bindings = proxy.config.Args.TryGetValue("connections", out var json)
                ? JsonSerializer.Deserialize<Dictionary<string, ConnectionBinding>>(json, JsonSerializerOptions.Web) : null;
            if (bindings is null || !bindings.TryGetValue(name, out var binding))
                throw new ConnectionException("access-denied", "The agent does not declare this connection.");
            return (service, binding);
        }
        public Task<string> GetSessionVersionAsync(string connection, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            return service.GetSessionVersionAsync(proxy.fabrcoreAgentHost.GetUserHandle(), proxy.fabrcoreAgentHost.GetHandle(), binding, cancellationToken);
        }
        public Task<ConnectionAccessToken> GetAccessTokenAsync(string connection, string resource, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            return service.GetAccessTokenAsync(proxy.fabrcoreAgentHost.GetUserHandle(), proxy.fabrcoreAgentHost.GetHandle(), binding, resource, cancellationToken);
        }
        public Task<HttpClient> GetHttpClientAsync(string connection, string resource, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            return service.GetHttpClientAsync(proxy.fabrcoreAgentHost.GetUserHandle(), proxy.fabrcoreAgentHost.GetHandle(), binding, resource, cancellationToken);
        }
    }
}
