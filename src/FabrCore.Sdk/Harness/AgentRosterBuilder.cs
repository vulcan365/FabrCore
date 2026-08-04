using FabrCore.Core;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// One agent projected into a prompt-ready roster entry.
/// </summary>
public sealed class AgentRosterEntry
{
    /// <summary>The agent's full FabrCore handle.</summary>
    public required string Handle { get; init; }

    /// <summary>A short, unique, non-empty name the model refers to this agent by.</summary>
    public required string Name { get; init; }

    /// <summary>What this agent is for, assembled from its configuration and registry metadata.</summary>
    public required string Description { get; init; }

    /// <summary>Why this agent cannot be used, or <see langword="null"/> when it is usable.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>True when the agent is configured and responded to a health probe.</summary>
    public bool IsAvailable => string.IsNullOrWhiteSpace(UnavailableReason);
}

/// <summary>
/// The result of projecting a set of agent handles into roster entries.
/// </summary>
public sealed class AgentRoster
{
    private IReadOnlyList<AgentRosterEntry>? available;
    private IReadOnlyList<AgentRosterEntry>? unavailable;

    /// <summary>Every handle that was asked about, available or not, in the order given.</summary>
    public required IReadOnlyList<AgentRosterEntry> Entries { get; init; }

    /// <summary>Entries that can actually be delegated to.</summary>
    public IReadOnlyList<AgentRosterEntry> Available =>
        available ??= Entries.Where(entry => entry.IsAvailable).ToList();

    /// <summary>Entries that were excluded, each carrying its reason.</summary>
    public IReadOnlyList<AgentRosterEntry> Unavailable =>
        unavailable ??= Entries.Where(entry => !entry.IsAvailable).ToList();

    /// <summary>A human-readable summary of what was excluded and why. Empty when nothing was.</summary>
    public string DescribeUnavailable() =>
        string.Join("; ", Unavailable.Select(entry => $"{entry.Name} ({entry.UnavailableReason})"));
}

/// <summary>
/// Projects FabrCore agent handles into named, described roster entries by joining registry metadata with
/// live health probes.
/// </summary>
/// <remarks>
/// <para>
/// Generalized from the Surface squad capability loader. The immediate consumer is the harness's background
/// agent list, whose provider rejects agents with empty or duplicate names — this is where those names get
/// resolved and de-duplicated, before construction can throw.
/// </para>
/// <para>
/// Every probe is individually fault-tolerant: an agent that fails registry lookup still gets an entry, and
/// an agent that fails its health probe gets an entry carrying the reason rather than disappearing.
/// </para>
/// </remarks>
public static class AgentRosterBuilder
{
    /// <summary>Descriptions longer than this are truncated with a note. Keeps rosters out of the context budget.</summary>
    public const int DescriptionCap = 500;

    /// <summary>
    /// Builds a roster for the given handles.
    /// </summary>
    /// <param name="handles">Agent handles to probe. Blank entries and duplicates are ignored.</param>
    /// <param name="agentHost">Host used for the health probes.</param>
    /// <param name="registry">Optional type registry supplying descriptions and declared capabilities.</param>
    /// <param name="logger">Optional logger.</param>
    public static async Task<AgentRoster> BuildAsync(
        IEnumerable<string> handles,
        IFabrCoreAgentHost agentHost,
        IFabrCoreRegistry? registry = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(agentHost);

        var entries = new List<AgentRosterEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawHandle in handles)
        {
            if (string.IsNullOrWhiteSpace(rawHandle))
            {
                continue;
            }

            var handle = rawHandle.Trim();
            if (!seenHandles.Add(handle))
            {
                logger?.LogDebug("Agent roster skipping duplicate handle - Handle: {Handle}", handle);
                continue;
            }

            AgentHealthStatus? health = null;
            string? unavailableReason = null;

            try
            {
                health = await agentHost.GetAgentHealth(handle, HealthDetailLevel.Detailed);
                if (health?.IsConfigured != true)
                {
                    unavailableReason = "Agent is not configured.";
                }
            }
            catch (Exception ex)
            {
                unavailableReason = ex.Message;
                logger?.LogWarning(ex, "Agent roster health probe failed - Handle: {Handle}", handle);
            }

            var registryEntry = TryFindRegistryEntry(registry, health?.Configuration?.AgentType, handle, logger);

            entries.Add(new AgentRosterEntry
            {
                Handle = handle,
                Name = ResolveName(handle, usedNames),
                Description = BuildDescription(handle, health, registryEntry),
                UnavailableReason = unavailableReason
            });
        }

        var roster = new AgentRoster { Entries = entries };

        logger?.LogInformation(
            "Agent roster built - Requested: {Requested}, Available: {Available}, Unavailable: {Unavailable}",
            entries.Count,
            roster.Available.Count,
            roster.Unavailable.Count);

        return roster;
    }

    /// <summary>The portion of a handle after the principal prefix, or the whole handle when there is none.</summary>
    public static string ShortHandle(string handle)
    {
        var separator = handle.IndexOf(':');
        return separator >= 0 && separator < handle.Length - 1
            ? handle[(separator + 1)..]
            : handle;
    }

    private static RegistryEntry? TryFindRegistryEntry(
        IFabrCoreRegistry? registry,
        string? agentType,
        string handle,
        ILogger? logger)
    {
        if (registry is null)
        {
            return null;
        }

        try
        {
            var shortHandle = ShortHandle(handle);

            return registry.GetAgentTypes().FirstOrDefault(entry =>
                entry.Aliases.Any(alias =>
                    (!string.IsNullOrWhiteSpace(agentType) && string.Equals(alias, agentType, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(alias, shortHandle, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Agent roster registry lookup failed - Handle: {Handle}", handle);
            return null;
        }
    }

    private static string ResolveName(string handle, HashSet<string> usedNames)
    {
        var candidate = ShortHandle(handle).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "agent";
        }

        var name = candidate;
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = $"{candidate}-{suffix++}";
        }

        return name;
    }

    private static string BuildDescription(string handle, AgentHealthStatus? health, RegistryEntry? registryEntry)
    {
        var description = health?.Configuration?.Description;

        if (string.IsNullOrWhiteSpace(description))
        {
            description = registryEntry?.Description;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            description = $"Agent {ShortHandle(handle)}";
        }

        if (!string.IsNullOrWhiteSpace(registryEntry?.Capabilities))
        {
            description = $"{description}{Environment.NewLine}Capabilities: {registryEntry.Capabilities.Trim()}";
        }

        if (description.Length > DescriptionCap)
        {
            var trimmed = description.Length - DescriptionCap;
            description = $"{description[..DescriptionCap]}... [truncated, {trimmed} more chars]";
        }

        return description;
    }
}
