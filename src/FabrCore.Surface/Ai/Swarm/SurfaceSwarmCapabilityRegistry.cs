using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSwarmCapabilityCard
{
    public string Name { get; set; } = string.Empty;

    public string Handle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public SurfaceSwarmSquadMemberRole Role { get; set; }

    public string Description { get; set; } = string.Empty;

    public List<string> Plugins { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public string? Notes { get; set; }

    public bool IsConfigured { get; set; }

    public string? UnavailableReason { get; set; }
}

public static class SurfaceSwarmCapabilityProjection
{
    private const int DescriptionCap = 500;

    public static SurfaceSwarmCapabilityCard Build(
        SurfaceSwarmSquadAgent squadAgent,
        RegistryEntry? registryEntry,
        AgentHealthStatus? health,
        string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(squadAgent);

        var description = !string.IsNullOrWhiteSpace(squadAgent.Description)
            ? squadAgent.Description!
            : !string.IsNullOrWhiteSpace(health?.Configuration?.Description)
                ? health!.Configuration!.Description!
                : !string.IsNullOrWhiteSpace(registryEntry?.Description)
                    ? registryEntry!.Description
                    : $"Agent {squadAgent.Name}";

        if (!string.IsNullOrWhiteSpace(registryEntry?.Capabilities))
        {
            description = string.IsNullOrWhiteSpace(description)
                ? registryEntry!.Capabilities
                : $"{description}\nCapabilities: {registryEntry!.Capabilities}";
        }

        if (description.Length > DescriptionCap)
        {
            description = description[..DescriptionCap] + $"... [truncated, {description.Length - DescriptionCap} more chars]";
        }

        var notes = registryEntry?.Notes is { Count: > 0 } noteList
            ? string.Join(Environment.NewLine, noteList.Select(note => $"- {note}"))
            : null;

        return new SurfaceSwarmCapabilityCard
        {
            Name = squadAgent.Name,
            Handle = squadAgent.Handle,
            AgentType = squadAgent.AgentType,
            Role = squadAgent.Role,
            Description = description,
            Plugins = health?.Configuration?.Plugins is { Count: > 0 } plugins
                ? [.. plugins]
                : [],
            Tools = health?.Configuration?.Tools is { Count: > 0 } tools
                ? [.. tools]
                : [],
            Notes = notes,
            IsConfigured = health?.IsConfigured == true,
            UnavailableReason = unavailableReason
        };
    }
}

public sealed class SurfaceSwarmCapabilityRegistry
{
    private readonly IFabrCoreRegistry? registry;
    private readonly IFabrCoreAgentHost agentHost;
    private readonly ILogger logger;

    public SurfaceSwarmCapabilityRegistry(
        IFabrCoreRegistry? registry,
        IFabrCoreAgentHost agentHost,
        ILogger logger)
    {
        this.registry = registry;
        this.agentHost = agentHost;
        this.logger = logger;
    }

    public async Task<List<SurfaceSwarmCapabilityCard>> BuildCardsAsync(SurfaceSwarmSquad squad)
    {
        var cards = new List<SurfaceSwarmCapabilityCard>();
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
                logger.LogDebug(
                    ex,
                    "Swarm registry lookup failed - AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    squadAgent.Handle,
                    squadAgent.AgentType);
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
                logger.LogWarning(
                    ex,
                    "Swarm squad agent health check failed - AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    squadAgent.Handle,
                    squadAgent.AgentType);
            }

            cards.Add(SurfaceSwarmCapabilityProjection.Build(squadAgent, registryEntry, health, unavailableReason));
        }

        return cards;
    }

    public static string FormatForPrompt(IReadOnlyList<SurfaceSwarmCapabilityCard> cards)
    {
        if (cards.Count == 0)
        {
            return "(no member agents)";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, cards.Select(card =>
            $"""
            - name: {card.Name}
              handle: {card.Handle}
              type: {card.AgentType}
              role: {FormatRole(card.Role)}
              status: {(card.IsConfigured ? "configured" : $"unavailable: {card.UnavailableReason ?? "not configured"}")}
              description: {card.Description}
              plugins: {string.Join(", ", card.Plugins)}
              tools: {string.Join(", ", card.Tools)}
              notes: {card.Notes}
            """));
    }

    private static string FormatRole(SurfaceSwarmSquadMemberRole role)
        => role switch
        {
            SurfaceSwarmSquadMemberRole.SubjectMatterExpert => "SubjectMatterExpert (consult-only, never assign tasks)",
            SurfaceSwarmSquadMemberRole.Helper => "Helper (context only, never assign tasks)",
            _ => "Executor (assignable)"
        };

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
