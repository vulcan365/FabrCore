using System.Text;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Core.Skills;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>Where an exposed agent came from.</summary>
public enum A2AExposureSource
{
    /// <summary>Named explicitly in the <c>A2A</c> configuration section.</summary>
    Configured,

    /// <summary>Discovered from the FabrCore agent-type registry.</summary>
    Registry,

    /// <summary>Discovered from the live agent list in the cluster.</summary>
    LiveAgent,
}

/// <summary>
/// One FabrCore agent published over A2A, with every setting already resolved from the agent's own
/// configuration, the <c>A2A:Defaults</c> block, and the FabrCore registry.
/// </summary>
public sealed class A2AExposedAgent
{
    /// <summary>Route segment and card identity, for example <c>support</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path this agent is served at, for example <c>/a2a/support</c>.</summary>
    public required string BasePath { get; init; }

    /// <summary>Human-readable name written to the agent card.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Description an orchestrator reads to decide when to route work here.</summary>
    public required string Description { get; init; }

    /// <summary>How this agent came to be published.</summary>
    public required A2AExposureSource Source { get; init; }

    /// <summary>Agent type alias provisioned per caller. Null when routing to a fixed handle.</summary>
    public string? AgentType { get; init; }

    /// <summary>Handle used for provisioned instances. Null when routing to a fixed handle.</summary>
    public string? ProvisionHandle { get; init; }

    /// <summary>Fully-qualified handle of a pre-existing agent. Null when provisioning per caller.</summary>
    public string? FixedHandle { get; init; }

    /// <summary>Model configuration name for provisioned instances.</summary>
    public required string Models { get; init; }

    /// <summary>System prompt for provisioned instances.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Plugin aliases enabled on provisioned instances.</summary>
    public required IReadOnlyList<string> Plugins { get; init; }

    /// <summary>Standalone tool aliases enabled on provisioned instances.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>Args passed to provisioned instances.</summary>
    public required IReadOnlyDictionary<string, string> Args { get; init; }

    /// <summary>Give each A2A <c>contextId</c> its own agent instance.</summary>
    public required bool AgentPerContext { get; init; }

    /// <summary>Media types accepted, for the card.</summary>
    public required IReadOnlyList<string> InputModes { get; init; }

    /// <summary>Media types produced, for the card.</summary>
    public required IReadOnlyList<string> OutputModes { get; init; }

    /// <summary>Whether the card advertises streaming.</summary>
    public required bool Streaming { get; init; }

    /// <summary>Agent version reported on the card.</summary>
    public required string Version { get; init; }

    /// <summary>Icon URL for the card.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Documentation URL for the card.</summary>
    public string? DocumentationUrl { get; init; }

    /// <summary>Skills advertised on the card.</summary>
    public required IReadOnlyList<A2ASkillOptions> Skills { get; init; }

    /// <summary><c>[FabrCoreNote]</c> values carried through from the registry.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// FabrCore harness skills this agent loads, parsed from its <c>_HarnessSkills</c> arg.
    /// </summary>
    public required IReadOnlyList<FabrCoreSkillReference> HarnessSkills { get; init; }

    /// <summary>
    /// Principal whose stored skill catalog describes <see cref="HarnessSkills"/>, or null when
    /// that depends on which caller shows up and so cannot be resolved for a shared card.
    /// </summary>
    public string? HarnessSkillPrincipal { get; init; }
}

/// <summary>Lookup for the agents this server publishes over A2A.</summary>
public interface IA2AAgentCatalog
{
    /// <summary>Every exposed agent: configured first, then discovered, in stable order.</summary>
    ValueTask<IReadOnlyList<A2AExposedAgent>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds an exposed agent by route name. Case-insensitive.</summary>
    ValueTask<A2AExposedAgent?> FindAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The agent served from the server-root well-known card, if one is designated.</summary>
    ValueTask<A2AExposedAgent?> GetPrimaryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the published agent set from three sources: explicit configuration, the FabrCore
/// agent-type registry, and the live agent list.
/// </summary>
/// <remarks>
/// Configuration and registry results are fixed for the lifetime of the process and are computed
/// once. Live-agent discovery reads cluster state, so it is cached for
/// <see cref="A2ADiscoveryOptions.RefreshInterval"/> and only runs at all when
/// <see cref="A2ADiscoveryOptions.IncludeAgentHandles"/> is non-empty — a server that does not use
/// it pays nothing.
/// </remarks>
internal sealed class A2AAgentCatalog : IA2AAgentCatalog
{
    private readonly A2AOptions _options;
    private readonly IFabrCoreAgentService _agentService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<A2AAgentCatalog> _logger;
    private readonly string _prefix;

    private readonly IReadOnlyList<A2AExposedAgent> _static;
    private readonly Dictionary<string, A2AExposedAgent> _staticByName;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<A2AExposedAgent> _live = Array.Empty<A2AExposedAgent>();
    private DateTimeOffset _liveExpiresAt = DateTimeOffset.MinValue;

    public A2AAgentCatalog(
        IOptions<A2AOptions> options,
        IFabrCoreRegistry registry,
        IFabrCoreAgentService agentService,
        TimeProvider timeProvider,
        ILogger<A2AAgentCatalog> logger)
    {
        _options = options.Value;
        _agentService = agentService;
        _timeProvider = timeProvider;
        _logger = logger;
        _prefix = NormalizeRoutePrefix(_options.RoutePrefix);

        var builder = new Builder(_options, _prefix, registry, logger);
        _static = builder.BuildStatic();
        _staticByName = _static.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        if (_static.Count > 0)
        {
            logger.LogInformation(
                "A2A publishes {Count} agent(s) from configuration and registry discovery: {Agents}",
                _static.Count,
                string.Join(", ", _static.Select(a => $"{a.Name} ({a.Source})")));
        }
    }

    /// <summary>True when live-agent discovery is configured and the catalog can change at runtime.</summary>
    public bool IsDynamic => _options.Discovery.IncludeAgentHandles.Count > 0;

    public async ValueTask<IReadOnlyList<A2AExposedAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDynamic)
        {
            return _static;
        }

        var live = await GetLiveAsync(cancellationToken);
        if (live.Count == 0)
        {
            return _static;
        }

        // Configured and registry agents win on a name collision: an operator's explicit intent
        // outranks whatever happens to be running.
        return _static.Concat(live.Where(a => !_staticByName.ContainsKey(a.Name))).ToList();
    }

    public async ValueTask<A2AExposedAgent?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_staticByName.TryGetValue(name, out var configured))
        {
            return configured;
        }

        if (!IsDynamic)
        {
            return null;
        }

        var live = await GetLiveAsync(cancellationToken);
        return live.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<A2AExposedAgent?> GetPrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.PrimaryAgent))
        {
            return await FindAsync(_options.PrimaryAgent!, cancellationToken);
        }

        var all = await ListAsync(cancellationToken);
        return all.Count == 1 ? all[0] : null;
    }

    private async ValueTask<IReadOnlyList<A2AExposedAgent>> GetLiveAsync(CancellationToken cancellationToken)
    {
        if (_timeProvider.GetUtcNow() < _liveExpiresAt)
        {
            return _live;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_timeProvider.GetUtcNow() < _liveExpiresAt)
            {
                return _live;
            }

            try
            {
                var agents = await _agentService.GetAgentsAsync("active");
                _live = BuildFromLiveAgents(agents);
            }
            catch (Exception ex)
            {
                // A cluster hiccup must not take down agents that are already published. Serve the
                // last known set and try again on the next request.
                _logger.LogWarning(
                    ex, "A2A live-agent discovery failed; serving the previously discovered set of {Count} agent(s).",
                    _live.Count);
            }

            _liveExpiresAt = _timeProvider.GetUtcNow() + _options.Discovery.RefreshInterval;
            return _live;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private IReadOnlyList<A2AExposedAgent> BuildFromLiveAgents(IEnumerable<AgentInfo> agents)
    {
        var discovery = _options.Discovery;
        var claimed = new HashSet<string>(_staticByName.Keys, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<A2AExposedAgent>();

        // Sorted so a handle collision resolves the same way on every refresh and every node.
        foreach (var info in agents.Where(a => Matches(a.Key, discovery.IncludeAgentHandles, discovery.ExcludeAgentHandles))
                     .OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var bare = Slug(info.Handle);
            var name = bare;
            if (name.Length == 0 || !claimed.Add(name))
            {
                // The bare handle is taken — by a configured agent, or by another principal's
                // agent with the same handle. Publish under the fully-qualified name rather than
                // dropping it: it matched the operator's glob, so it should stay reachable.
                name = Slug(info.Key);
                if (name.Length == 0 || !claimed.Add(name))
                {
                    _logger.LogWarning(
                        "A2A could not publish live agent {Key}: both '{Bare}' and '{Qualified}' are taken.",
                        info.Key, bare, name);
                    continue;
                }

                _logger.LogInformation(
                    "A2A publishes live agent {Key} as '{Qualified}' because the route name '{Bare}' is already taken.",
                    info.Key, name, bare);
            }

            var displayName = TitleCase(name);
            var description = $"{displayName}, a FabrCore agent published as {info.Key}.";

            resolved.Add(Build(
                name,
                displayName,
                description,
                source: A2AExposureSource.LiveAgent,
                agentType: null,
                provisionHandle: null,
                fixedHandle: info.Key,
                agent: null,
                notes: Array.Empty<string>(),
                skills: null));
        }

        return resolved;
    }

    private A2AExposedAgent Build(
        string name,
        string displayName,
        string description,
        A2AExposureSource source,
        string? agentType,
        string? provisionHandle,
        string? fixedHandle,
        A2AAgentOptions? agent,
        IReadOnlyList<string> notes,
        IReadOnlyList<A2ASkillOptions>? skills)
        => BuildCore(_options, _prefix, name, displayName, description, source, agentType,
            provisionHandle, fixedHandle, agent, notes, skills);

    /// <summary>Merges an agent's own settings over <c>A2A:Defaults</c>.</summary>
    private static A2AExposedAgent BuildCore(
        A2AOptions options,
        string prefix,
        string name,
        string displayName,
        string description,
        A2AExposureSource source,
        string? agentType,
        string? provisionHandle,
        string? fixedHandle,
        A2AAgentOptions? agent,
        IReadOnlyList<string> notes,
        IReadOnlyList<A2ASkillOptions>? skills)
    {
        var defaults = options.Defaults;

        var args = new Dictionary<string, string>(defaults.Args);
        if (agent is not null)
        {
            foreach (var (key, value) in agent.Args)
            {
                args[key] = value;
            }
        }

        return new A2AExposedAgent
        {
            Name = name,
            BasePath = $"{prefix}/{name}",
            DisplayName = displayName,
            Description = description,
            Source = source,
            AgentType = agentType,
            ProvisionHandle = provisionHandle,
            FixedHandle = fixedHandle,
            Models = Pick(agent?.Models, defaults.Models),
            SystemPrompt = agent?.SystemPrompt ?? defaults.SystemPrompt,
            Plugins = agent?.Plugins is { Count: > 0 } p ? p : defaults.Plugins,
            Tools = agent?.Tools is { Count: > 0 } t ? t : defaults.Tools,
            Args = args,
            AgentPerContext = agent?.AgentPerContext ?? defaults.AgentPerContext,
            InputModes = agent?.InputModes is { Count: > 0 } im ? im : defaults.InputModes,
            OutputModes = agent?.OutputModes is { Count: > 0 } om ? om : defaults.OutputModes,
            Streaming = agent?.Streaming ?? defaults.Streaming,
            Version = Pick(agent?.Version, defaults.Version),
            IconUrl = agent?.IconUrl,
            DocumentationUrl = agent?.DocumentationUrl,
            Notes = notes,
            Skills = skills ?? SynthesizeSkills(name, displayName, description, notes, capabilities: null),
            HarnessSkills = ParseHarnessSkills(args),
            HarnessSkillPrincipal = ResolveHarnessSkillPrincipal(options, fixedHandle),
        };
    }

    /// <summary>
    /// Reads the <c>_HarnessSkills</c> arg — a CSV of <c>name@version</c> references — into parsed
    /// references, ignoring anything malformed the same way the harness itself does.
    /// </summary>
    private static IReadOnlyList<FabrCoreSkillReference> ParseHarnessSkills(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue(FabrCore.Sdk.HarnessArgs.Skills, out var csv) || string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<FabrCoreSkillReference>();
        }

        var references = new List<FabrCoreSkillReference>();
        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FabrCoreSkillReference.TryParse(token, out var reference, out _) && reference is not null)
            {
                references.Add(reference);
            }
        }

        return references;
    }

    /// <summary>
    /// Harness skills are principal-scoped, and an agent card is served to every caller alike —
    /// often before any caller has authenticated. So resolve the principal only where it does not
    /// depend on the caller: an agent published by handle carries its principal in that handle,
    /// and a provisioned agent has a fixed principal only under the Fixed strategy.
    /// </summary>
    private static string? ResolveHarnessSkillPrincipal(A2AOptions options, string? fixedHandle)
    {
        if (fixedHandle is { Length: > 0 })
        {
            var separator = fixedHandle.IndexOf(':');
            return separator > 0 ? fixedHandle[..separator] : null;
        }

        if (options.Principal.Strategy != A2APrincipalStrategy.Fixed)
        {
            return null;
        }

        var handle = Slug(options.Principal.Handle);
        return handle.Length == 0
            ? null
            : (string.IsNullOrWhiteSpace(options.Principal.Prefix) ? handle : options.Principal.Prefix + handle);
    }

    /// <summary>
    /// A card must advertise at least one skill for an orchestrator to route to it. When none are
    /// configured, build one out of what the registry already knows: the description, the declared
    /// capabilities as tags, and any notes as examples of when the agent applies.
    /// </summary>
    private static IReadOnlyList<A2ASkillOptions> SynthesizeSkills(
        string name, string displayName, string description, IReadOnlyList<string> notes, string? capabilities)
    {
        var tags = new List<string> { "fabrcore" };
        if (!string.IsNullOrWhiteSpace(capabilities))
        {
            tags.AddRange(capabilities!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length <= 40)
                .Take(8));
        }

        return new List<A2ASkillOptions>
        {
            new()
            {
                Id = name,
                Name = displayName,
                Description = description,
                Tags = tags,
                Examples = notes.Count > 0 ? notes.Take(5).ToList() : new List<string>(),
            },
        };
    }

    /// <summary>Builds the fixed part of the catalog: explicit configuration plus registry discovery.</summary>
    private sealed class Builder
    {
        private readonly A2AOptions _options;
        private readonly string _prefix;
        private readonly IFabrCoreRegistry _registry;
        private readonly ILogger _logger;
        private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<A2AExposedAgent> _resolved = new();

        public Builder(A2AOptions options, string prefix, IFabrCoreRegistry registry, ILogger logger)
        {
            _options = options;
            _prefix = prefix;
            _registry = registry;
            _logger = logger;
        }

        public IReadOnlyList<A2AExposedAgent> BuildStatic()
        {
            var registryByAlias = BuildRegistryIndex();

            // Explicit entries first so they win the route name against a shorthand or a
            // discovered agent describing the same thing.
            foreach (var agent in _options.Agents)
            {
                AddConfigured(agent, registryByAlias);
            }

            foreach (var agentType in _options.AgentTypes.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                AddConfigured(new A2AAgentOptions { AgentType = agentType.Trim() }, registryByAlias);
            }

            foreach (var handle in _options.AgentHandles.Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                AddConfigured(new A2AAgentOptions { AgentHandle = handle.Trim() }, registryByAlias);
            }

            AddRegistryDiscovered();

            return _resolved;
        }

        private void AddConfigured(A2AAgentOptions agent, Dictionary<string, RegistryEntry> registryByAlias)
        {
            var name = Slug(agent.Name ?? agent.AgentType ?? StripPrincipal(agent.AgentHandle) ?? string.Empty);
            if (name.Length == 0)
            {
                _logger.LogWarning(
                    "Skipping an A2A:Agents entry with no Name, AgentType, or AgentHandle to derive a route name from.");
                return;
            }

            if (!_claimed.Add(name))
            {
                _logger.LogDebug("A2A agent '{Name}' is already published; ignoring the duplicate entry.", name);
                return;
            }

            registryByAlias.TryGetValue(agent.AgentType ?? string.Empty, out var entry);
            Add(name, agent, entry, A2AExposureSource.Configured);
        }

        private void AddRegistryDiscovered()
        {
            var discovery = _options.Discovery;
            if (discovery.AgentTypes == A2ADiscoveryMode.None)
            {
                return;
            }

            // GetAgentTypes already omits [FabrCoreHidden] types, so hiding an agent from
            // /fabrcoreapi/discovery hides it from A2A with no second switch to remember.
            foreach (var entry in _registry.GetAgentTypes().OrderBy(e => e.TypeName, StringComparer.Ordinal))
            {
                var alias = entry.Aliases.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                if (alias is null)
                {
                    continue;
                }

                if (!Matches(alias, discovery.IncludeAgentTypes, discovery.ExcludeAgentTypes))
                {
                    continue;
                }

                if (discovery.AgentTypes == A2ADiscoveryMode.Described
                    && string.IsNullOrWhiteSpace(entry.Description))
                {
                    _logger.LogDebug(
                        "A2A discovery skipped agent type '{Alias}': no [Description] and AgentTypes is 'Described'.",
                        alias);
                    continue;
                }

                var name = Slug(alias);
                if (name.Length == 0 || !_claimed.Add(name))
                {
                    continue;
                }

                Add(name, new A2AAgentOptions { AgentType = alias }, entry, A2AExposureSource.Registry);
            }
        }

        private void Add(string name, A2AAgentOptions agent, RegistryEntry? entry, A2AExposureSource source)
        {
            var displayName = Coalesce(agent.DisplayName, TitleCase(name));
            var notes = _options.Discovery.IncludeNotes && entry?.Notes is { Count: > 0 }
                ? entry.Notes
                : (IReadOnlyList<string>)Array.Empty<string>();

            var description = Coalesce(
                agent.Description,
                entry?.Description,
                entry?.Capabilities,
                $"{displayName}, a FabrCore agent.");

            var skills = agent.Skills.Count > 0
                ? agent.Skills.Select(s => new A2ASkillOptions
                {
                    Id = Coalesce(s.Id, Slug(s.Name), name),
                    Name = Coalesce(s.Name, displayName),
                    Description = Coalesce(s.Description, description),
                    Tags = s.Tags.Count > 0 ? s.Tags : new List<string> { "fabrcore" },
                    Examples = s.Examples,
                }).ToList()
                : SynthesizeSkills(name, displayName, description, notes, entry?.Capabilities);

            _resolved.Add(BuildCore(
                _options,
                _prefix,
                name,
                displayName,
                description,
                source,
                agentType: agent.AgentType,
                provisionHandle: agent.AgentHandle is null ? Coalesce(agent.Handle, $"a2a-{name}") : null,
                fixedHandle: agent.AgentHandle,
                agent: agent,
                notes: notes,
                skills: skills));
        }

        private Dictionary<string, RegistryEntry> BuildRegistryIndex()
        {
            var index = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _registry.GetAgentTypes())
            {
                foreach (var alias in entry.Aliases)
                {
                    index[alias] = entry;
                }

                index.TryAdd(entry.TypeName, entry);
            }

            return index;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches a value against include/exclude glob lists. An empty include list means "everything";
    /// an exclude match always wins.
    /// </summary>
    internal static bool Matches(string value, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        if (include.Count > 0 && !include.Any(pattern => GlobMatches(value, pattern)))
        {
            return false;
        }

        return !exclude.Any(pattern => GlobMatches(value, pattern));
    }

    private static bool GlobMatches(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.Contains('*'))
        {
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? StripPrincipal(string? qualifiedHandle)
    {
        if (string.IsNullOrWhiteSpace(qualifiedHandle))
        {
            return null;
        }

        var separator = qualifiedHandle.IndexOf(':');
        return separator >= 0 ? qualifiedHandle[(separator + 1)..] : qualifiedHandle;
    }

    /// <summary>Normalizes a configured route prefix to a leading slash with no trailing slash.</summary>
    internal static string NormalizeRoutePrefix(string? prefix)
    {
        var value = (prefix ?? A2ADefaults.RoutePrefix).Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.StartsWith('/') ? value : "/" + value;
    }

    /// <summary>Lowercases and hyphenates a value so it is safe as a single URL path segment.</summary>
    internal static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string TitleCase(string slug)
        => string.Join(' ', slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static string Coalesce(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
}
