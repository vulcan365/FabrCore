using System.ComponentModel;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Dedicated search plugin for the <see cref="GraphRagSearchAgent"/>.
/// Exposes a quick hybrid <see cref="Search"/> tool and a deeper
/// <see cref="DeepSearch"/> evidence-loop tool, both scope-filtered by
/// <see cref="IKnowledgeSearchService"/>.
///
/// Scopes are read from <c>AllowedScopes</c> in the plugin/agent config at
/// init time and baked in. The LLM never sees a scopes parameter.
///
/// Domain classification is a soft taxonomy hint — it can narrow the filter
/// for better ranking, but it is NEVER treated as security. Scope is the
/// only access boundary.
/// </summary>
[PluginAlias("graph-rag-query")]
public class GraphRagQueryPlugin : GraphRagPluginBase
{
    protected override string PluginAlias => "graph-rag-query";

    private IKnowledgeSearchService? _searchService;
    private IReadOnlyList<string>? _allowedScopes;
    private DomainIntentClassifier? _classifier;

    /// <summary>
    /// Sets the domain intent classifier for automatic query-time domain
    /// detection. Called by the search agent during initialization.
    /// </summary>
    internal void SetClassifier(DomainIntentClassifier classifier) => _classifier = classifier;

    public override Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _searchService = serviceProvider.GetRequiredService<IKnowledgeSearchService>();

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

    private IReadOnlyList<string> GetAllowedScopes()
    {
        return _allowedScopes
            ?? throw new InvalidOperationException(
                "AllowedScopes is not configured. Set AllowedScopes in the plugin " +
                "or agent Args (e.g. \"AllowedScopes\": \"scope1,scope2\").");
    }

    [Description("Search the knowledge base. Performs hybrid vector + graph search within this agent's allowed scopes. Returns entities, chunks, and relationships with provenance.")]
    public async Task<string> Search(
        [Description("The search query — a question or topic to find relevant knowledge about")] string query,
        [Description("How many graph hops to traverse from each result (1-3, default 2)")] int graphDepth = 2,
        [Description("Maximum number of initial vector results to expand (default 10)")] int limit = 10,
        [Description("Optional domain name to filter results (taxonomy only, not a security boundary). If omitted and autoClassifyDomain is true, the domain is auto-detected.")] string? domainFilter = null,
        [Description("Automatically detect the query's domain intent for filtering (default true)")] bool autoClassifyDomain = true)
    {
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        limit = Math.Clamp(limit, 1, 20);

        // Soft taxonomy hint: let the classifier pick a domain if the caller
        // didn't supply one. This only tightens the search; scope is still
        // enforced underneath.
        if (domainFilter is null && autoClassifyDomain && _classifier is not null)
        {
            try
            {
                var classification = await _classifier.ClassifyQueryAsync(query);
                if (classification.PrimaryDomain is not null && classification.Confidence >= 0.5)
                    domainFilter = classification.PrimaryDomain;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Domain intent classification failed — continuing without domain filter");
            }
        }

        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: limit,
            DomainFilter: domainFilter);

        return await _searchService!.HybridSearchAsync(request, graphDepth: graphDepth, vectorLimit: limit);
    }

    [Description("Deep-search the knowledge base for broad reports, investigations, and complete-picture questions. Performs a bounded evidence loop over vector hits and scoped graph relationships, returning structured JSON evidence.")]
    public async Task<string> DeepSearch(
        [Description("The search query — a question or topic to investigate deeply")] string query,
        [Description("How many graph hops to traverse from each discovered entity (1-3, default 2)")] int graphDepth = 2,
        [Description("Maximum number of initial vector results to seed the evidence loop (default 10)")] int vectorLimit = 10,
        [Description("Maximum evidence-loop iterations (1-3, default 2)")] int maxIterations = 2,
        [Description("Optional domain name to filter results (taxonomy only, not a security boundary). If omitted and autoClassifyDomain is true, the domain is auto-detected.")] string? domainFilter = null,
        [Description("Automatically detect the query's domain intent for filtering (default true)")] bool autoClassifyDomain = true)
    {
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        vectorLimit = Math.Clamp(vectorLimit, 1, 20);
        maxIterations = Math.Clamp(maxIterations, 1, 3);

        if (domainFilter is null && autoClassifyDomain && _classifier is not null)
        {
            try
            {
                var classification = await _classifier.ClassifyQueryAsync(query);
                if (classification.PrimaryDomain is not null && classification.Confidence >= 0.5)
                    domainFilter = classification.PrimaryDomain;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Domain intent classification failed — continuing without domain filter");
            }
        }

        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: GetAllowedScopes(),
            Limit: vectorLimit,
            DomainFilter: domainFilter);

        return await _searchService!.DeepSearchAsync(
            request,
            graphDepth: graphDepth,
            vectorLimit: vectorLimit,
            maxIterations: maxIterations);
    }
}
