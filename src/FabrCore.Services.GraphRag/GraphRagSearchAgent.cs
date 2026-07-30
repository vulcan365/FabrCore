using System.ComponentModel;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

[AgentAlias("graph-rag-search-agent")]
public class GraphRagSearchAgent : FabrCoreAgentProxy
{
    /// <summary>
    /// Args key for the comma-separated list of scopes this agent is allowed
    /// to search. Required — an agent without allowed scopes will fail fast
    /// at initialization time. List order is informational only; all listed
    /// scopes are searched on equal footing and ranking is driven purely by
    /// raw vector distance.
    /// </summary>
    public const string AllowedScopesArgKey = "AllowedScopes";

    private AIAgent? _agent;
    private AgentSession? _session;

    private const string DefaultSearchSystemPrompt = """
        You are a knowledge graph search agent. You answer questions ONLY using data from the Search and DeepSearch tools.

        RULES:
        - You MUST call Search or DeepSearch for EVERY question before answering.
        - Use DeepSearch for broad reports, investigations, complete-picture questions, and deep dives.
        - Use Search for quick targeted lookup.
        - Base your answer ONLY on the data returned by the search tools.
        - Do NOT answer from your own training data or general knowledge.
        - If the search tools return no results, say "No relevant information found in the knowledge base."
        - When presenting results, cite entity names, relationship types, and content from the search results.
        - When results include provenance (Domain > Category), mention the source domain and category to help the user understand where the information comes from. For example: "According to Equipment > Maintenance: ..."
        - If results span multiple domains, organize your response by domain.
        - If the user asks about something not in the search results, say so clearly.
        """;

    public GraphRagSearchAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var connectionStringName = config.Args?.GetValueOrDefault("ConnectionStringName")
            ?? throw new InvalidOperationException("ConnectionStringName arg is required");

        _ = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found in configuration");

        // Allowed scopes — the hard partition this agent is bound to. Fail
        // fast if missing or empty. The LLM will never see a scopes parameter;
        // every search call uses this list verbatim.
        var allowedScopesRaw = config.Args?.GetValueOrDefault(AllowedScopesArgKey)
            ?? throw new InvalidOperationException(
                $"GraphRagSearchAgent requires Args[\"{AllowedScopesArgKey}\"] — " +
                "a comma-separated list of scope keys this agent may search.");

        var allowedScopes = allowedScopesRaw
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (allowedScopes.Length == 0)
            throw new InvalidOperationException(
                $"GraphRagSearchAgent Args[\"{AllowedScopesArgKey}\"] must contain at least one scope key.");

        // Warn about missing scopes but allow the agent to start. The scopes
        // may be created later via the admin UI or ingestion pipeline. Searches
        // against a missing scope will simply return zero results. The schema
        // itself may not exist yet on an empty database, so catch SQL errors.
        try
        {
            var scopeService = serviceProvider.GetRequiredService<IKnowledgeScopeService>();
            foreach (var scope in allowedScopes)
            {
                if (!await scopeService.ScopeExistsAsync(scope))
                    logger.LogWarning(
                        "GraphRagSearchAgent: scope '{Scope}' is not yet registered. " +
                        "Create it via the admin UI or scope plugin before sending queries.", scope);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "GraphRagSearchAgent: Could not verify scopes (database schema may not exist yet). " +
                "The agent will start but searches may fail until the schema is initialized.");
        }

        logger.LogInformation(
            "GraphRagSearchAgent initializing with connection '{Name}', allowed scopes: {Scopes}",
            connectionStringName, string.Join(", ", allowedScopes));

        // Resolve the authoritative search surface. All SQL lives here; the
        // agent never touches the database directly.
        var searchService = serviceProvider.GetRequiredService<IKnowledgeSearchService>();

        // Build the scope-pinned facade. Its search methods are the ONLY
        // knowledge tools the LLM will see — no scopes parameter, so the LLM
        // cannot broaden or narrow its own access.
        var facade = new ScopedKnowledgeFacade(searchService, allowedScopes);

        // Domain classifier stays for soft taxonomy hints. It needs the
        // domain plugin for the list of existing domain names.
        var domainPlugin = new GraphRagDomainPlugin();
        await domainPlugin.InitializeAsync(config, serviceProvider);
        try
        {
            var chatClientConfigName = config.Models ?? "default";
            var chatClient = await chatClientService.GetChatClient(chatClientConfigName);
            facade.SetClassifier(new DomainIntentClassifier(chatClient, domainPlugin, logger));
            logger.LogInformation("GraphRagSearchAgent initialized DomainIntentClassifier with model '{Model}'", chatClientConfigName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize DomainIntentClassifier — domain intent classification disabled");
        }

        var tools = await ResolveConfiguredToolsAsync();

        // Register the facade's scoped search methods. The LLM has no other
        // path into the knowledge base.
        var searchMethod = typeof(ScopedKnowledgeFacade).GetMethod(nameof(ScopedKnowledgeFacade.Search))!;
        tools.Add(AIFunctionFactory.Create(searchMethod, facade));
        var deepSearchMethod = typeof(ScopedKnowledgeFacade).GetMethod(nameof(ScopedKnowledgeFacade.DeepSearch))!;
        tools.Add(AIFunctionFactory.Create(deepSearchMethod, facade));

        if (string.IsNullOrWhiteSpace(config.SystemPrompt))
            config.SystemPrompt = DefaultSearchSystemPrompt;

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);

        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);

        SetStatusMessage("Searching knowledge...");

        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }

        return response;
    }

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;
}

/// <summary>
/// Scope-pinned wrapper over <see cref="IKnowledgeSearchService"/>. The
/// constructor captures an immutable list of allowed scopes, and the search
/// methods are registered as AIFunctions with no scopes parameter — so the
/// LLM cannot override the binding. These are the only knowledge tools
/// exposed by <see cref="GraphRagSearchAgent"/>.
/// </summary>
internal sealed class ScopedKnowledgeFacade
{
    private readonly IKnowledgeSearchService _searchService;
    private readonly IReadOnlyList<string> _scopes;
    private DomainIntentClassifier? _classifier;

    public ScopedKnowledgeFacade(IKnowledgeSearchService searchService, IReadOnlyList<string> scopes)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        if (_scopes.Count == 0)
            throw new ArgumentException("At least one scope is required", nameof(scopes));
    }

    public void SetClassifier(DomainIntentClassifier classifier) => _classifier = classifier;

    [Description("Search the knowledge base. Performs a hybrid vector + graph search within the scopes this agent is permitted to access. Returns entities, chunks, and relationships with provenance.")]
    public async Task<string> Search(
        [Description("The search query — a question or topic to find relevant knowledge about")] string query,
        [Description("How many graph hops to traverse from each result (1-3, default 2)")] int graphDepth = 2,
        [Description("Maximum number of initial vector results to expand (default 10)")] int limit = 10)
    {
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        limit = Math.Clamp(limit, 1, 20);

        // Soft taxonomy hint — optional domain narrowing via the classifier.
        // Scope is still the authoritative access boundary.
        string? domainFilter = null;
        if (_classifier is not null)
        {
            try
            {
                var classification = await _classifier.ClassifyQueryAsync(query);
                if (classification.PrimaryDomain is not null && classification.Confidence >= 0.5)
                    domainFilter = classification.PrimaryDomain;
            }
            catch
            {
                // Classifier failures are non-fatal.
            }
        }

        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: _scopes,
            Limit: limit,
            DomainFilter: domainFilter);

        return await _searchService.HybridSearchAsync(request, graphDepth: graphDepth, vectorLimit: limit);
    }

    [Description("Deep-search the knowledge base. Performs a bounded vector + graph evidence loop within the scopes this agent is permitted to access. Use for reports, investigations, complete-picture questions, and deep dives. Returns structured JSON evidence.")]
    public async Task<string> DeepSearch(
        [Description("The search query — a question or topic to investigate deeply")] string query,
        [Description("How many graph hops to traverse from each discovered entity (1-3, default 2)")] int graphDepth = 2,
        [Description("Maximum number of initial vector results to seed the evidence loop (default 10)")] int vectorLimit = 10,
        [Description("Maximum evidence-loop iterations (1-3, default 2)")] int maxIterations = 2)
    {
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        vectorLimit = Math.Clamp(vectorLimit, 1, 20);
        maxIterations = Math.Clamp(maxIterations, 1, 3);

        string? domainFilter = null;
        if (_classifier is not null)
        {
            try
            {
                var classification = await _classifier.ClassifyQueryAsync(query);
                if (classification.PrimaryDomain is not null && classification.Confidence >= 0.5)
                    domainFilter = classification.PrimaryDomain;
            }
            catch
            {
                // Classifier failures are non-fatal.
            }
        }

        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: _scopes,
            Limit: vectorLimit,
            DomainFilter: domainFilter);

        return await _searchService.DeepSearchAsync(
            request,
            graphDepth: graphDepth,
            vectorLimit: vectorLimit,
            maxIterations: maxIterations);
    }
}
