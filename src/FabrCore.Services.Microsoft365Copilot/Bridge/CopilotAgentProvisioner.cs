using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Resolves the FabrCore agent that should answer a Copilot conversation and makes sure it
/// exists, provisioning it from <see cref="CopilotAgentOptions"/> on first contact.
/// </summary>
public interface ICopilotAgentProvisioner
{
    /// <summary>
    /// Returns the agent handle to send to (bare for same-principal agents, fully qualified for
    /// shared agents), ensuring the agent is configured for the principal.
    /// </summary>
    Task<string> EnsureAgentAsync(string principalHandle, string? conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached ensure result so the next message re-verifies the agent (call after a
    /// send failure, e.g. when the agent was evicted).
    /// </summary>
    void Invalidate(string principalHandle, string agentHandle);
}

internal sealed class CopilotAgentProvisioner : ICopilotAgentProvisioner
{
    private readonly IFabrCoreAgentService _agentService;
    private readonly Microsoft365CopilotOptions _options;
    private readonly ILogger<CopilotAgentProvisioner> _logger;
    private readonly ConcurrentDictionary<string, Task> _ensured = new();

    public CopilotAgentProvisioner(
        IFabrCoreAgentService agentService,
        IOptions<Microsoft365CopilotOptions> options,
        ILogger<CopilotAgentProvisioner> logger)
    {
        _agentService = agentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> EnsureAgentAsync(
        string principalHandle, string? conversationId, CancellationToken cancellationToken)
    {
        var agentOptions = _options.Agent;

        if (!string.IsNullOrWhiteSpace(agentOptions.SharedAgentHandle))
        {
            await EnsureSharedAgentAsync(agentOptions);
            return agentOptions.SharedAgentHandle!;
        }

        var handle = agentOptions.Handle;
        if (agentOptions.AgentPerConversation && !string.IsNullOrWhiteSpace(conversationId))
        {
            handle = $"{handle}-{CopilotHandleSanitizer.SanitizeAgentHandleFragment(conversationId)}";
        }

        var cacheKey = $"{principalHandle}:{handle}";
        try
        {
            await _ensured.GetOrAdd(cacheKey, _ => EnsureCoreAsync(principalHandle, handle));
        }
        catch
        {
            // Do not cache failures — the next message should retry provisioning.
            _ensured.TryRemove(cacheKey, out _);
            throw;
        }

        return handle;
    }

    public void Invalidate(string principalHandle, string agentHandle)
        => _ensured.TryRemove($"{principalHandle}:{agentHandle}", out _);

    private async Task EnsureCoreAsync(string principalHandle, string handle)
    {
        var config = BuildConfiguration(handle);
        var results = await _agentService.EnsureAgentsAsync(principalHandle, [config]);

        var status = results.FirstOrDefault();
        if (status is null || status.State == HealthState.Unhealthy || status.State == HealthState.NotConfigured)
        {
            throw new InvalidOperationException(
                $"Failed to provision Copilot agent '{handle}' (type '{config.AgentType}') for principal '{principalHandle}': " +
                $"{status?.State.ToString() ?? "no result"} — {status?.Message ?? "no details"}. " +
                "Verify Microsoft365Copilot:Agent:AgentType matches a registered [AgentAlias] and the agent's assembly " +
                "is listed in FabrCoreServerOptions.AdditionalAssemblies.");
        }

        _logger.LogInformation(
            "Copilot agent {Handle} ready for principal {Principal} ({State})",
            handle, principalHandle, status.State);
    }

    private async Task EnsureSharedAgentAsync(CopilotAgentOptions agentOptions)
    {
        // Shared agents are only auto-provisioned for the system principal, and only when the
        // host told us how to build one. Any other shared handle is assumed to be provisioned by
        // the host application (blueprints, startup code, ...).
        const string systemPrefix = "system:";
        var shared = agentOptions.SharedAgentHandle!;
        if (string.IsNullOrWhiteSpace(agentOptions.AgentType)
            || !shared.StartsWith(systemPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _ensured.GetOrAdd("shared|" + shared, async _ =>
        {
            var config = BuildConfiguration(shared[systemPrefix.Length..]);
            var status = await _agentService.ConfigureSystemAgentAsync(config);
            _logger.LogInformation(
                "Shared Copilot system agent {Handle} configured ({State})", shared, status.State);
        });
    }

    private AgentConfiguration BuildConfiguration(string handle) => new()
    {
        Handle = handle,
        AgentType = _options.Agent.AgentType,
        Models = _options.Agent.Models,
        SystemPrompt = _options.Agent.SystemPrompt,
        Plugins = [.. _options.Agent.Plugins],
        Tools = [.. _options.Agent.Tools],
        Args = new Dictionary<string, string>(_options.Agent.Args),
    };
}
