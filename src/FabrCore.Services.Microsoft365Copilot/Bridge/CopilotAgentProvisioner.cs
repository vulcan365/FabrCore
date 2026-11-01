using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Makes sure the FabrCore agent behind an exposed <see cref="CopilotAgent"/> exists for the
/// calling principal.
/// </summary>
public interface ICopilotAgentProvisioner
{
    /// <summary>
    /// Returns the agent handle to send to, ensuring the agent exists under the principal. An
    /// agent that is already there — provisioned by a blueprint, say — is left exactly as it is.
    /// </summary>
    Task<string> EnsureAgentAsync(CopilotAgent agent, string principalHandle, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached ensure result so the next message re-verifies the agent (call after a
    /// send failure, e.g. when the agent was evicted).
    /// </summary>
    void Invalidate(string principalHandle, string agentHandle);
}

internal sealed class CopilotAgentProvisioner : ICopilotAgentProvisioner
{
    private readonly IFabrCoreAgentService _agentService;
    private readonly ILogger<CopilotAgentProvisioner> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new();

    public CopilotAgentProvisioner(
        IFabrCoreAgentService agentService,
        ILogger<CopilotAgentProvisioner> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    public async Task<string> EnsureAgentAsync(
        CopilotAgent agent, string principalHandle, CancellationToken cancellationToken)
    {
        var cacheKey = $"{principalHandle}:{agent.Handle}";
        try
        {
            await _ensured.GetOrAdd(cacheKey, _ => new Lazy<Task>(() => EnsureCoreAsync(agent, principalHandle)))
                .Value.WaitAsync(cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Do not cache failures — the next message should retry provisioning.
            _ensured.TryRemove(cacheKey, out _);
            throw;
        }

        return agent.Handle;
    }

    public void Invalidate(string principalHandle, string agentHandle)
    {
        _ensured.TryRemove($"{principalHandle}:{agentHandle}", out _);
    }

    private async Task EnsureCoreAsync(CopilotAgent agent, string principalHandle)
    {
        // Type and model only. Plugins, tools, and prompts are the agent author's or the
        // blueprint's to set; an agent that already exists is not reconfigured by this call.
        var config = new AgentConfiguration
        {
            Handle = agent.Handle,
            AgentType = agent.AgentType,
            Models = agent.Models,
        };
        var results = await _agentService.EnsureAgentsAsync(principalHandle, [config]);

        var status = results.FirstOrDefault();
        if (status is null || status.State == HealthState.Unhealthy || status.State == HealthState.NotConfigured)
        {
            throw new InvalidOperationException(
                $"Failed to provision Copilot agent '{agent.Handle}' (type '{config.AgentType}') for principal '{principalHandle}': " +
                $"{status?.State.ToString() ?? "no result"} — {status?.Message ?? "no details"}. " +
                $"Verify the AgentType of Microsoft365Copilot agent '{agent.Name}' matches a registered [AgentAlias] and the agent's assembly " +
                "is present in the application dependency graph. AdditionalAssemblies is needed only for assemblies outside that graph.");
        }

        _logger.LogInformation(
            "Copilot agent {Handle} ready for principal {Principal} ({State})",
            agent.Handle, principalHandle, status.State);
    }
}
