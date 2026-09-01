using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Resolves the FabrCore agent handle that answers an A2A request and makes sure the agent
/// exists, provisioning it from configuration on first contact.
/// </summary>
public interface IA2AAgentProvisioner
{
    /// <summary>
    /// Returns the handle to send to — bare for agents owned by the calling principal, fully
    /// qualified for a shared agent — ensuring it is configured.
    /// </summary>
    Task<string> EnsureAgentAsync(
        A2AExposedAgent agent, string principalHandle, string? contextId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached ensure result so the next request re-verifies the agent. Call after a
    /// send failure, for example when the agent was evicted.
    /// </summary>
    void Invalidate(string principalHandle, string agentHandle);
}

internal sealed class A2AAgentProvisioner : IA2AAgentProvisioner
{
    private readonly IFabrCoreAgentService _agentService;
    private readonly ILogger<A2AAgentProvisioner> _logger;
    private readonly ConcurrentDictionary<string, Task> _ensured = new();

    public A2AAgentProvisioner(IFabrCoreAgentService agentService, ILogger<A2AAgentProvisioner> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    public async Task<string> EnsureAgentAsync(
        A2AExposedAgent agent, string principalHandle, string? contextId, CancellationToken cancellationToken)
    {
        if (agent.FixedHandle is not null)
        {
            // A pre-existing agent: the host owns its lifecycle (blueprint, startup code, or an
            // earlier API call), so we route to it without touching its configuration.
            return agent.FixedHandle;
        }

        var handle = agent.ProvisionHandle!;
        if (agent.AgentPerContext && !string.IsNullOrWhiteSpace(contextId))
        {
            handle = $"{handle}-{A2AAgentCatalog.Slug(contextId)}";
        }

        var cacheKey = $"{principalHandle}:{handle}";
        try
        {
            await _ensured.GetOrAdd(cacheKey, _ => EnsureCoreAsync(agent, principalHandle, handle));
        }
        catch
        {
            // Never cache a failure — the next request should retry provisioning.
            _ensured.TryRemove(cacheKey, out _);
            throw;
        }

        return handle;
    }

    public void Invalidate(string principalHandle, string agentHandle)
        => _ensured.TryRemove($"{principalHandle}:{agentHandle}", out _);

    private async Task EnsureCoreAsync(A2AExposedAgent agent, string principalHandle, string handle)
    {
        var config = new AgentConfiguration
        {
            Handle = handle,
            AgentType = agent.AgentType,
            Models = agent.Models,
            SystemPrompt = agent.SystemPrompt,
            Description = agent.Description,
            Plugins = [.. agent.Plugins],
            Tools = [.. agent.Tools],
            Args = new Dictionary<string, string>(agent.Args),
        };

        var results = await _agentService.EnsureAgentsAsync(principalHandle, [config]);
        var status = results.FirstOrDefault();

        if (status is null || status.State is HealthState.Unhealthy or HealthState.NotConfigured)
        {
            throw new InvalidOperationException(
                $"Failed to provision A2A agent '{handle}' (type '{config.AgentType}') for principal " +
                $"'{principalHandle}': {status?.State.ToString() ?? "no result"} — {status?.Message ?? "no details"}. " +
                "Verify the A2A agent's AgentType matches a registered [AgentAlias] and that the agent's assembly " +
                "is present in the application dependency graph.");
        }

        _logger.LogInformation(
            "A2A agent {Handle} ready for principal {Principal} ({State})", handle, principalHandle, status.State);
    }
}
