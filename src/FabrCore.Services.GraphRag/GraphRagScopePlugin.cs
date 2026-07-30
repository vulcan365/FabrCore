using System.ComponentModel;
using System.Text.Json;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// LLM tool adapter for managing the scope registry. The old
/// <c>AssignEntityToScope</c> / <c>RemoveEntityFromScope</c> tools are gone —
/// scope is now an intrinsic column on <c>KnowledgeEntity</c>, set at ingestion
/// time by the deterministic pipeline and never by the LLM. What remains is
/// lightweight registry management: create a scope, list existing ones.
/// </summary>
[PluginAlias("graph-rag-scope")]
[Description("Knowledge scope registry plugin — create and list access-control scope keys.")]
[FabrCoreCapabilities("Registers scope keys used by the GraphRAG knowledge base to partition entities for access control. Scope is pinned at ingestion time by deterministic code; this plugin only manages the registry of valid keys.")]
[FabrCoreNote("Scope cannot be assigned to entities via LLM tools — that is done by the ingestion agent from deterministic state args.")]
public class GraphRagScopePlugin : GraphRagPluginBase
{
    protected override string PluginAlias => "graph-rag-scope";

    private IKnowledgeScopeService? _scopeService;

    public override Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _scopeService = serviceProvider.GetRequiredService<IKnowledgeScopeService>();
        return base.InitializeAsync(config, serviceProvider);
    }

    [Description("Create a new knowledge scope key. Scope keys partition the knowledge graph for access control — every entity is ingested under a single scope, and every search filters by an allowed scope list.")]
    public async Task<string> CreateScope(
        [Description("A unique key for the scope (e.g. 'job-ops', 'job-ops-manager', 'hr')")] string scopeKey,
        [Description("A brief description of what this scope represents")] string description,
        [Description("Optional default priority weight used as a tiebreaker when no caller-supplied ordering exists (default 1.0)")] double defaultPriority = 1.0,
        [Description("Optional JSON metadata")] string? metadata = null)
    {
        try
        {
            var scope = await _scopeService!.CreateScopeAsync(scopeKey, description, defaultPriority, metadata);
            return JsonSerializer.Serialize(new
            {
                scope.ScopeKey,
                scope.Description,
                scope.DefaultPriority,
                scope.CreatedAt,
                message = "Scope created successfully."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CreateScope failed for '{ScopeKey}'", scopeKey);
            return $"Error creating scope: {ex.Message}";
        }
    }

    [Description("List all defined knowledge scopes, including the entity count in each.")]
    public async Task<string> ListScopes()
    {
        try
        {
            var scopes = await _scopeService!.ListScopesAsync();
            if (scopes.Count == 0)
                return "No scopes defined.";

            var rows = new List<object>(scopes.Count);
            foreach (var s in scopes)
            {
                var count = await _scopeService.CountEntitiesInScopeAsync(s.ScopeKey);
                rows.Add(new
                {
                    s.ScopeKey,
                    s.Description,
                    s.DefaultPriority,
                    s.CreatedAt,
                    entityCount = count
                });
            }

            return JsonSerializer.Serialize(rows, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ListScopes failed");
            return $"Error listing scopes: {ex.Message}";
        }
    }
}
