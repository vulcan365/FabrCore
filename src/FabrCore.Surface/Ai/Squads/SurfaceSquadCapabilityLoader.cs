using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Orchestration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Squads;

/// <summary>
/// Builds the prompt-ready capability roster for a squad by joining squad member metadata with
/// <see cref="IFabrCoreRegistry"/> entries and live agent health. Shared by every squad coordinator
/// so the roster is projected the same way regardless of squad type.
/// </summary>
public static class SurfaceSquadCapabilityLoader
{
    /// <summary>
    /// Projects every member of <paramref name="squad"/> into a <see cref="SurfaceSquadAgentCapability"/>.
    /// Registry lookup and health checks are individually fault-tolerant: a member whose health probe throws
    /// is still returned, carrying the failure in <see cref="SurfaceSquadAgentCapability.UnavailableReason"/>.
    /// </summary>
    /// <param name="squad">The squad whose members should be projected.</param>
    /// <param name="agentHost">Host used to probe member health.</param>
    /// <param name="registry">Optional registry used to enrich descriptions with agent-type metadata.</param>
    /// <param name="includeRoleNote">When true, appends the member's role to <see cref="SurfaceSquadAgentCapability.Notes"/>.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public static async Task<List<SurfaceSquadAgentCapability>> BuildAsync(
        SurfaceSquad squad,
        IFabrCoreAgentHost agentHost,
        IFabrCoreRegistry? registry,
        bool includeRoleNote = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentNullException.ThrowIfNull(agentHost);

        var capabilities = new List<SurfaceSquadAgentCapability>();
        foreach (var squadAgent in squad.Agents)
        {
            RegistryEntry? registryEntry = null;
            try
            {
                registryEntry = registry?.GetAgentTypes()
                    .FirstOrDefault(entry => entry.Aliases.Any(alias =>
                        string.Equals(alias, squadAgent.AgentType, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(alias, ShortHandle(squadAgent.Handle), StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                logger?.LogDebug(
                    ex,
                    "Squad registry lookup failed - AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    squadAgent.Handle,
                    squadAgent.AgentType);
                registryEntry = null;
            }

            AgentHealthStatus? health = null;
            string? unavailableReason = null;
            try
            {
                health = await agentHost.GetAgentHealth(squadAgent.Handle, HealthDetailLevel.Detailed);
                if (health?.IsConfigured != true)
                {
                    unavailableReason = "Agent is not configured.";
                }
            }
            catch (Exception ex)
            {
                unavailableReason = ex.Message;
                logger?.LogWarning(
                    ex,
                    "Squad agent health check failed - AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    squadAgent.Handle,
                    squadAgent.AgentType);
            }

            var capability = SurfaceSquadAgentCapabilityProjection.Build(
                squadAgent,
                registryEntry,
                health,
                unavailableReason);

            if (includeRoleNote)
            {
                capability.Notes = string.IsNullOrWhiteSpace(capability.Notes)
                    ? $"Role: {squadAgent.Role}"
                    : $"{capability.Notes}{Environment.NewLine}Role: {squadAgent.Role}";
            }

            capabilities.Add(capability);
        }

        logger?.LogInformation(
            "Squad capabilities built - Squad: {SquadName}, CapabilityCount: {CapabilityCount}, ConfiguredCount: {ConfiguredCount}, UnavailableCount: {UnavailableCount}",
            squad.Name,
            capabilities.Count,
            capabilities.Count(capability => capability.IsConfigured),
            capabilities.Count(capability => !capability.IsConfigured));

        return capabilities;
    }

    /// <summary>Returns the alias portion of a <c>principal:alias</c> handle, or the handle itself when unqualified.</summary>
    public static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
