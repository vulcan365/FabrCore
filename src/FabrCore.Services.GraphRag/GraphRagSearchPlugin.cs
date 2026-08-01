using System.ComponentModel;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// LLM tool adapter over <see cref="IKnowledgeSearchService"/>. Scopes are
/// read from <c>AllowedScopes</c> in the plugin/agent config at init time
/// and baked into every search request. The LLM never sees a scopes
/// parameter — it cannot broaden or narrow its own access.
/// </summary>
[PluginAlias("graph-rag-search")]
public class GraphRagSearchPlugin : GraphRagPluginBase
{
    protected override string PluginAlias => "graph-rag-search";

    private IKnowledgeSearchService? _searchService;
    private IReadOnlyList<string>? _allowedScopes;

    public override Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _searchService = serviceProvider.GetRequiredService<IKnowledgeSearchService>();

        // AllowedScopes is optional at init — when not configured, tool calls
        // will throw with a clear error. This allows programmatic-only usage.
        var scopesRaw = config.GetPluginSetting(PluginAlias, "AllowedScopes")
            ?? config.Args?.GetValueOrDefault("AllowedScopes");

        if (!string.IsNullOrWhiteSpace(scopesRaw))
        {
            _allowedScopes = scopesRaw
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();

            if (_allowedScopes.Count == 0)
                _allowedScopes = null;
        }

        return base.InitializeAsync(config, serviceProvider);
    }

    /// <summary>
    /// Returns the configured allowed scopes, or throws if not configured.
    /// </summary>
    protected IReadOnlyList<string> GetAllowedScopes()
    {
        return _allowedScopes
            ?? throw new InvalidOperationException(
                "AllowedScopes is not configured. Set AllowedScopes in the plugin " +
                "or agent Args (e.g. \"AllowedScopes\": \"scope1,scope2\").");
    }

    [Description("Search the knowledge graph for entities matching a query using vector similarity. Returns entities with Domain > Category provenance, ranked by raw vector distance.")]
    public Task<string> SearchKnowledge(
        [Description("The search query text to find relevant knowledge entities")] string query,
        [Description("Maximum number of results to return (default 10)")] int limit = 10,
        [Description("Optional filter by entity type (e.g. 'Person', 'Concept', 'Document')")] string? entityTypeFilter = null,
        [Description("Optional domain name to filter results (taxonomy only, not a security boundary)")] string? domainFilter = null)
    {
        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: limit,
            EntityTypeFilter: entityTypeFilter,
            DomainFilter: domainFilter);

        return _searchService!.SearchEntitiesAsync(request);
    }

    [Description("Search content chunks using vector similarity for fine-grained semantic search. Returns chunks with their parent entity info, Domain > Category provenance, and raw vector distance.")]
    public Task<string> SearchChunks(
        [Description("The search query text")] string query,
        [Description("Maximum number of chunk results to return (default 10)")] int limit = 10,
        [Description("Optional filter by parent entity type")] string? entityTypeFilter = null,
        [Description("Optional domain name to filter results (taxonomy only, not a security boundary)")] string? domainFilter = null)
    {
        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: limit,
            EntityTypeFilter: entityTypeFilter,
            DomainFilter: domainFilter);

        return _searchService!.SearchChunksAsync(request);
    }

    [Description("Find entities related to a given entity by traversing the knowledge graph edges. Both endpoints of every returned edge are scope-checked, so no cross-scope walks are possible. Supports filtering by relationship type and multi-hop depth (1-3).")]
    public Task<string> SearchRelationships(
        [Description("The name of the entity to find relationships for")] string entityName,
        [Description("The type of the entity (e.g. 'Document', 'Concept')")] string entityType,
        [Description("Optional filter by relationship type (e.g. 'RELATED_TO', 'PART_OF')")] string? relationshipTypeFilter = null,
        [Description("Graph traversal depth, 1-3 hops (default 1)")] int depth = 1)
    {
        var request = new ScopedRelationshipRequest(
            EntityName: entityName,
            EntityType: entityType,
            Scopes: GetAllowedScopes(),
            RelationshipTypeFilter: relationshipTypeFilter,
            Depth: Math.Clamp(depth, 1, 3));

        return _searchService!.SearchRelationshipsAsync(request);
    }

    [Description("Hybrid GraphRAG search: vector search on entities + chunks, then graph expansion by traversing relationships. Returns enriched results with provenance.")]
    public Task<string> HybridSearch(
        [Description("The search query text")] string query,
        [Description("How many hops to traverse from each vector result (1-3, default 1)")] int graphDepth = 1,
        [Description("How many initial vector results to expand (default 5)")] int vectorLimit = 5,
        [Description("Optional domain filter (taxonomy only, not a security boundary)")] string? domainFilter = null)
    {
        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: vectorLimit,
            DomainFilter: domainFilter);

        return _searchService!.HybridSearchAsync(request, graphDepth, vectorLimit);
    }

    [Description("Deep GraphRAG search: bounded evidence loop for reports, investigations, and complete-picture questions. Returns structured JSON evidence with entities, chunks, relationships, taxonomy, metadata, iteration summaries, and coverage.")]
    public Task<string> DeepSearch(
        [Description("The search query text")] string query,
        [Description("How many hops to traverse from each discovered entity (1-3, default 2)")] int graphDepth = 2,
        [Description("How many initial vector results to seed the evidence loop (default 10)")] int vectorLimit = 10,
        [Description("Maximum evidence-loop iterations (1-3, default 2)")] int maxIterations = 2,
        [Description("Optional domain filter (taxonomy only, not a security boundary)")] string? domainFilter = null)
    {
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        vectorLimit = Math.Clamp(vectorLimit, 1, 20);
        maxIterations = Math.Clamp(maxIterations, 1, 3);

        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: vectorLimit,
            DomainFilter: domainFilter);

        return _searchService!.DeepSearchAsync(request, graphDepth, vectorLimit, maxIterations);
    }
}
