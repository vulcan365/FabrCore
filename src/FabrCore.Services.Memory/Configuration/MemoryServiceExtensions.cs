using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Administration;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace FabrCore.Services.Memory.Configuration;

/// <summary>
/// Extension methods for registering agent memory services in the DI container.
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// Registers the agent memory services (three-temperature memory management,
    /// taxonomy enforcement, LLM-based retrieval, scoped shared memory, audit, and
    /// compaction) with self-contained SQL schema initialization.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// Name of the connection string in IConfiguration pointing to the SQL Server 2025 /
    /// Azure SQL database (VECTOR support required) that hosts the <c>mem</c> schema.
    /// </param>
    /// <param name="configure">Optional configuration callback for <see cref="AgentMemoryOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// On startup, automatically creates the <c>mem</c> schema and memory tables
    /// (<c>MemoryEntity</c>, <c>MemoryChunk</c>, <c>MemoryRelationship</c>,
    /// <c>MemorySummaryNode</c>, <c>MemoryScope</c>, <c>MemoryAuditLog</c>) if they
    /// do not already exist. Startup fails fast when the connection string is missing,
    /// schema creation fails, or <c>IEmbeddings</c> is not registered — set
    /// <see cref="AgentMemoryOptions.AllowStartupWithoutEmbeddings"/> to relax this
    /// for client-only hosts.
    /// </para>
    /// <para>
    /// <c>IEmbeddings</c> is provided by <c>AddFabrCoreServer()</c> with an "embeddings"
    /// model entry in fabrcore.json.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentMemoryServices(
        this IServiceCollection services,
        string connectionStringName,
        Action<AgentMemoryOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var options = new AgentMemoryOptions();
        configure?.Invoke(options);
        options.ConnectionStringName = connectionStringName;

        services.AddSingleton(options);
        services.AddSingleton<IMemoryAuditLog>(sp => new MemoryAuditLog(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<MemoryAuditLog>>(),
            connectionStringName));
        services.AddSingleton<IMemoryScopeService, MemoryScopeService>();
        services.AddHostedService<MemorySchemaHostedService>();
        services.AddSingleton<IMemoryStore, SqlMemoryStore>();
        services.AddSingleton<IMemoryIndexManager, MemoryIndexManager>();
        services.AddSingleton<IMemoryRetriever, MemoryRetriever>();
        services.AddSingleton<IMemoryCompactor, MemoryCompactor>();
        services.AddSingleton<IRetrievalPlanner, RetrievalPlanner>();
        services.AddSingleton<IMemorySummaryTree, MemorySummaryTreeBuilder>();
        services.AddSingleton<IAgentMemoryProvider, AgentMemoryProvider>();
        services.AddSingleton<MemoryAwareCompactionService>();
        services.AddSingleton<ISyntheticImaginingService, SyntheticImaginingService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IMemoryAdminService"/> — the administration surface used by
    /// admin UIs (e.g. the FabrCore.Surface.Admin memory page) and maintenance tooling.
    /// Requires <see cref="AddAgentMemoryServices"/> to be called first.
    /// </summary>
    public static IServiceCollection AddMemoryAdministration(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryAdminService>(sp => new MemoryAdminService(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<AgentMemoryOptions>(),
            sp,
            sp.GetRequiredService<ILogger<MemoryAdminService>>()));
        services.AddKeyedSingleton<IMemoryAdminClient>(MemoryAdminClientKeys.Local, (sp, _) =>
            new LocalMemoryAdminClient(sp.GetRequiredService<IMemoryAdminService>()));
        services.AddOptions<MemoryAdminClientOptions>();
        services.TryAddTransient<IMemoryAdminClient, MemoryAdminClientSelector>();

        services.AddControllers()
            .ConfigureApplicationPartManager(parts =>
            {
                if (parts.ApplicationParts.All(part => part.Name != typeof(MemoryServiceExtensions).Assembly.GetName().Name))
                {
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(MemoryServiceExtensions).Assembly));
                }
            });

        return services;
    }

    /// <summary>
    /// Registers the HTTP Memory administration client. Register an
    /// <see cref="IMemoryAdminPrincipalAccessor"/> that resolves the current principal.
    /// </summary>
    public static IServiceCollection AddRemoteMemoryAdministration(
        this IServiceCollection services,
        Action<MemoryAdminClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<MemoryAdminClientOptions>().Configure(configure);
        services.AddHttpClient<RemoteMemoryAdminClient>();
        services.TryAddTransient<IMemoryAdminClient, MemoryAdminClientSelector>();
        return services;
    }
}

internal class MemorySchemaHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemorySchemaHostedService> _logger;

    public MemorySchemaHostedService(
        IConfiguration configuration,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILogger<MemorySchemaHostedService> logger)
    {
        _configuration = configuration;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString(_options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var message =
                $"Agent memory connection string '{_options.ConnectionStringName}' was not found in configuration. " +
                "Add it under ConnectionStrings or pass a different name to AddAgentMemoryServices().";
            if (!_options.AllowStartupWithoutEmbeddings)
                throw new InvalidOperationException(message);

            _logger.LogError("{Message} Memory services will not function.", message);
            return;
        }

        _logger.LogInformation("Initializing agent memory schema on connection '{Name}'...",
            _options.ConnectionStringName);

        await MemorySchemaInitializer.EnsureSchemaAsync(connectionString, _options.EmbeddingDimensions, _logger);

        if (_serviceProvider.GetService<IEmbeddings>() is null)
        {
            var message =
                "Agent memory requires IEmbeddings, which is not registered. " +
                "Ensure AddFabrCoreServer() is configured with an 'embeddings' model entry in fabrcore.json, " +
                "or set AgentMemoryOptions.AllowStartupWithoutEmbeddings for client-only hosts.";
            if (!_options.AllowStartupWithoutEmbeddings)
                throw new InvalidOperationException(message);

            _logger.LogError("{Message} Saving or recalling memories will fail.", message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
