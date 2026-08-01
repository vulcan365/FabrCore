using System.Net.Http;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Core.Monitoring;
using FabrCore.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace FabrCore.Services.GraphRag;

public static class GraphRagServiceExtensions
{
    /// <summary>
    /// Registers GraphRAG services and ensures the SQL schema (grag.*) is
    /// created on startup. This single call wires up:
    ///
    /// <list type="bullet">
    ///   <item><see cref="IKnowledgeScopeService"/> — scope registry (<c>grag.KnowledgeScope</c>).</item>
    ///   <item><see cref="IKnowledgeSearchService"/> — authoritative search surface with mandatory scope enforcement.</item>
    ///   <item><see cref="GraphRagSchemaHostedService"/> — DDL bootstrap for the full <c>grag</c> schema (includes scope tables).</item>
    /// </list>
    ///
    /// The old separate <c>AddGraphRagKnowledgeScopes</c> call is gone — scope is
    /// now an intrinsic column on <c>grag.KnowledgeEntity</c>, so there is no
    /// secondary schema to bootstrap. Everything lives under one extension.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// The name of the connection string in IConfiguration (e.g., "GraphRagDb").
    /// Must point to a SQL Server 2025 or Azure SQL database with VECTOR support.
    /// </param>
    public static IServiceCollection AddGraphRagServices(
        this IServiceCollection services,
        string connectionStringName,
        string? extractionModelName = null)
    {
        services.TryAddSingleton<IMarkdownConversionService, PassThroughMarkdownConversionService>();
        services.TryAddSingleton<IFabrCoreHostApiClient>(sp =>
            new FabrCoreHostApiClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FabrCoreHostApiClient)),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<FabrCoreHostApiClient>>()));

        services.AddSingleton(new GraphRagOptions
        {
            ConnectionStringName = connectionStringName,
            ExtractionModelName = extractionModelName
        });
        services.AddHostedService<GraphRagSchemaHostedService>();

        // Action audit log. Plain DB service writing to grag.ActionAudit.
        // Registered before the services that consume it.
        services.AddSingleton<IGraphRagAuditLog>(sp =>
            new GraphRagAuditLog(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<GraphRagAuditLog>>(),
                connectionStringName));

        // Scope registry. Plain DB service with no LLM or embeddings dependency.
        services.AddSingleton<IKnowledgeScopeService>(sp =>
            new KnowledgeScopeService(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<KnowledgeScopeService>>(),
                connectionStringName,
                sp.GetRequiredService<IGraphRagAuditLog>()));

        // Authoritative search surface. Uses IEmbeddings when available (server),
        // falls back to Host API /fabrcoreapi/Embeddings on client-only hosts.
        services.AddSingleton<IKnowledgeSearchService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new KnowledgeSearchService(
                config,
                sp.GetRequiredService<ILogger<KnowledgeSearchService>>(),
                connectionStringName,
                sp.GetRequiredService<IGraphRagAuditLog>(),
                embeddings: sp.GetService<IEmbeddings>(),
                httpClientFactory: sp.GetService<IHttpClientFactory>(),
                hostApiBaseUrl: config[FabrCore.Core.FabrCoreConfigurationKeys.HostUrl]);
        });

        // Ingestion service. Uses IEmbeddings when available (server), falls back
        // to the FabrCore Host API /fabrcoreapi/Embeddings endpoint on client-only hosts.
        // LLM extraction resolves an explicit model first, then the conventional
        // "graphrag" model, then "default". It can be disabled through configuration.
        services.AddSingleton<IKnowledgeIngestionService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<GraphRagOptions>();
            return new KnowledgeIngestionService(
                config,
                sp.GetRequiredService<ILogger<KnowledgeIngestionService>>(),
                connectionStringName,
                sp.GetRequiredService<IGraphRagAuditLog>(),
                embeddings: sp.GetService<IEmbeddings>(),
                httpClientFactory: sp.GetService<IHttpClientFactory>(),
                hostApiBaseUrl: config[FabrCore.Core.FabrCoreConfigurationKeys.HostUrl],
                serviceProvider: sp,
                extractionModelName: opts.ExtractionModelName,
                agentMessageMonitor: sp.GetService<IAgentMessageMonitor>(),
                // IFabrCoreHostApiClient may be scoped (for example when Surface
                // supplies principal-aware request context). Never capture it in
                // this singleton; ingestion opens a short scope for each remote call.
                serviceScopeFactory: sp.GetRequiredService<IServiceScopeFactory>());
        });

        return services;
    }

    /// <summary>
    /// Registers the GraphRAG administration service surface. Requires
    /// <see cref="AddGraphRagServices"/> to have been called first (for
    /// <see cref="GraphRagOptions"/> and schema bootstrap).
    ///
    /// The Search tab requires <see cref="IKnowledgeSearchService"/> which depends on
    /// <c>IEmbeddings</c> (registered by <c>AddFabrCoreServer</c>). If the host does
    /// not have <c>AddFabrCoreServer</c>, all CRUD tabs work but Search will show an
    /// error message explaining the missing dependency.
    /// </summary>
    public static IServiceCollection AddGraphRagAdministration(this IServiceCollection services)
    {
        services.AddSingleton<IGraphRagAdminService>(sp =>
            new GraphRagAdminService(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<GraphRagOptions>(),
                sp,
                sp.GetRequiredService<ILogger<GraphRagAdminService>>()));

        services.AddSingleton(sp =>
            new LocalGraphRagAdminClient(
                sp.GetRequiredService<IGraphRagAdminService>(),
                sp.GetRequiredService<IKnowledgeIngestionService>(),
                sp.GetRequiredService<IKnowledgeScopeService>(),
                sp.GetRequiredService<IMarkdownConversionService>()));
        services.AddKeyedSingleton<IGraphRagAdminClient>(GraphRagAdminClientKeys.Local, (sp, _) =>
            new AclLocalGraphRagAdminClient(sp.GetRequiredService<LocalGraphRagAdminClient>(), sp));

        services.AddControllers()
            .ConfigureApplicationPartManager(parts =>
            {
                if (parts.ApplicationParts.All(part => part.Name != typeof(GraphRagServiceExtensions).Assembly.GetName().Name))
                {
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(GraphRagServiceExtensions).Assembly));
                }
            });

        return services;
    }
}

internal class GraphRagOptions
{
    public string ConnectionStringName { get; init; } = "";

    /// <summary>
    /// Optional explicit model configuration for LLM-based entity extraction.
    /// When null, ingestion prefers <c>graphrag</c> and falls back to
    /// <c>default</c>. Set <c>GraphRag:Ingestion:EnableExtraction=false</c>
    /// for document-entity + chunks only.
    /// </summary>
    public string? ExtractionModelName { get; init; }
}

internal class GraphRagSchemaHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly GraphRagOptions _options;
    private readonly ILogger<GraphRagSchemaHostedService> _logger;

    public GraphRagSchemaHostedService(
        IConfiguration configuration,
        GraphRagOptions options,
        ILogger<GraphRagSchemaHostedService> logger)
    {
        _configuration = configuration;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString(_options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{_options.ConnectionStringName}' not found in configuration");

        _logger.LogInformation("Initializing GraphRAG schema on connection '{Name}'...",
            _options.ConnectionStringName);

        await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, _logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
