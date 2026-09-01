using FabrCore.Core.Skills;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Reads a principal's stored FabrCore harness skills so an agent that loads them can advertise
/// them on its A2A agent card.
/// </summary>
/// <remarks>
/// These are two different things that share a word. A FabrCore <em>harness skill</em> is a
/// versioned, principal-scoped package of instructions and resources that an agent loads at
/// runtime (<c>_HarnessSkills</c>). An A2A <em>skill</em> is a line of card metadata that tells a
/// remote orchestrator what an agent is good for. They map cleanly onto each other — a harness
/// skill's name and description are a concrete statement of what the agent can now do — so when an
/// agent declares harness skills, the card can say so instead of describing the agent in the
/// abstract.
/// </remarks>
public interface IA2AHarnessSkillResolver
{
    /// <summary>
    /// Returns the stored catalog entries for the references <paramref name="agent"/> declares,
    /// or an empty list when the feature is off, the agent declares none, or the principal whose
    /// catalog to read is not knowable before the caller authenticates.
    /// </summary>
    ValueTask<IReadOnlyList<FabrCoreSkillCatalogEntry>> ResolveAsync(
        A2AExposedAgent agent, CancellationToken cancellationToken = default);
}

internal sealed class A2AHarnessSkillResolver : IA2AHarnessSkillResolver
{
    private readonly IFabrCoreSkillCatalogService? _catalog;
    private readonly A2AOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<A2AHarnessSkillResolver> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache = new();

    public A2AHarnessSkillResolver(
        IOptions<A2AOptions> options,
        TimeProvider timeProvider,
        ILogger<A2AHarnessSkillResolver> logger,
        IFabrCoreSkillCatalogService? catalog = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _catalog = catalog;
    }

    public async ValueTask<IReadOnlyList<FabrCoreSkillCatalogEntry>> ResolveAsync(
        A2AExposedAgent agent, CancellationToken cancellationToken = default)
    {
        if (!_options.Discovery.IncludeHarnessSkills
            || _catalog is null
            || agent.HarnessSkills.Count == 0
            || agent.HarnessSkillPrincipal is null)
        {
            return Array.Empty<FabrCoreSkillCatalogEntry>();
        }

        var published = await GetPublishedAsync(agent.HarnessSkillPrincipal, cancellationToken);
        if (published.Count == 0)
        {
            return Array.Empty<FabrCoreSkillCatalogEntry>();
        }

        var resolved = new List<FabrCoreSkillCatalogEntry>();
        foreach (var reference in agent.HarnessSkills)
        {
            if (published.TryGetValue(reference.ToString(), out var entry))
            {
                resolved.Add(entry);
            }
            else
            {
                // The agent references a skill this principal has not published. That is the
                // agent's problem to fix, not the card's — say so once and leave it off.
                _logger.LogWarning(
                    "A2A agent {Agent} declares harness skill {Reference}, which principal {Principal} has not published. "
                    + "It is omitted from the agent card.",
                    agent.Name, reference, agent.HarnessSkillPrincipal);
            }
        }

        return resolved;
    }

    private async ValueTask<IReadOnlyDictionary<string, FabrCoreSkillCatalogEntry>> GetPublishedAsync(
        string principal, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(principal, out var cached) && now < cached.ExpiresAt)
        {
            return cached.Entries;
        }

        try
        {
            var entries = await _catalog!.ListAsync(principal, cancellationToken);
            var index = entries.ToDictionary(e => e.Reference, StringComparer.OrdinalIgnoreCase);
            _cache[principal] = new CacheEntry(index, now + _options.Discovery.RefreshInterval);
            return index;
        }
        catch (Exception ex)
        {
            // A card that is missing its skill list is far better than a card that fails to serve,
            // because a client fetches the card before it can do anything else.
            _logger.LogWarning(
                ex, "Could not read the harness skill catalog for principal {Principal}; agent cards omit skills this round.",
                principal);

            var stale = cached.Entries ?? new Dictionary<string, FabrCoreSkillCatalogEntry>();
            _cache[principal] = new CacheEntry(stale, now + _options.Discovery.RefreshInterval);
            return stale;
        }
    }

    private readonly record struct CacheEntry(
        IReadOnlyDictionary<string, FabrCoreSkillCatalogEntry> Entries, DateTimeOffset ExpiresAt);
}
