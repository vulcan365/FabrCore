using System.Text.Json;
using FabrCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

public abstract partial class FabrCoreAgentProxy
{
    /// <summary>Optional, agent-bound authenticated connections for trusted agent/plugin code.</summary>
    protected IAgentConnections Connections => new AgentConnections(this);

    private sealed class AgentConnections(FabrCoreAgentProxy proxy) : IAgentConnections
    {
        /// <summary>Who is asking, from the host: the agent's principal, its handle and the type it runs as.</summary>
        private ConnectionCaller Caller => new(proxy.fabrcoreAgentHost.GetUserHandle(), proxy.fabrcoreAgentHost.GetHandle(), proxy.config.AgentType);

        private (IConnectionService Service, ConnectionBinding Binding) Resolve(string name)
        {
            if (AdminDiagnosticContext.Current.Value is not null)
                throw new ConnectionException("access-denied", "Diagnostic turns cannot access production connections.");
            var service = proxy.serviceProvider.GetService<IConnectionService>()
                ?? throw new ConnectionException("feature-disabled", "Authenticated connections are not enabled.");
            // Explicit opt-in. This reserved alias cannot be redirected by agent arguments.
            if (name == "$agent-identity") return (service, new ConnectionBinding(Caller.Principal, name));
            var bindings = proxy.config.Args.TryGetValue("connections", out var json)
                ? JsonSerializer.Deserialize<Dictionary<string, ConnectionBinding>>(json, JsonSerializerOptions.Web) : null;
            if (bindings is not null && bindings.TryGetValue(name, out var binding)) return (service, binding);
            // Undeclared means what the host's resolver says, or else the agent's own principal's connection of that name, so an
            // agent a channel created, or one created before the connection existed, can use it. The connection still decides.
            var resolved = proxy.serviceProvider.GetService<IConnectionBindingResolver>()?.Resolve(Caller, name);
            return (service, resolved ?? new ConnectionBinding(proxy.fabrcoreAgentHost.GetUserHandle(), name));
        }
        public Task<string> GetSessionVersionAsync(string connection, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            return service.GetSessionVersionAsync(Caller, binding, cancellationToken);
        }
        public async Task<ConnectionAccessToken> GetAccessTokenAsync(string connection, string resource, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            try { return await service.GetAccessTokenAsync(Caller, binding, resource, cancellationToken); }
            catch (ConnectionException e) when (e.Notice is not null) { await TellPersonAsync(e.Notice); throw; }
        }
        public async Task<HttpClient> GetHttpClientAsync(string connection, string resource, CancellationToken cancellationToken = default)
        {
            var (service, binding) = Resolve(connection);
            try { return await service.GetHttpClientAsync(Caller, binding, resource, TellPersonAsync, cancellationToken); }
            catch (ConnectionException e) when (connection == "$agent-identity" && e.Code == "identity-required")
            {
                await TellPersonAsync($"An administrator needs to link {Caller.Agent} to an agent identity before I can use {resource}.");
                throw;
            }
        }

        /// <summary>Best effort: failing to tell them mustn't hide why the agent stopped.</summary>
        private async Task TellPersonAsync(string notice)
        {
            try { await proxy.SendToUserAsync(notice); }
            catch (Exception e) { proxy.logger.LogWarning(e, "Couldn't tell the person their connection lapsed."); }
        }
    }
}
