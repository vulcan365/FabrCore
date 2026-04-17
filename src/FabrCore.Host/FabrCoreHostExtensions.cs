using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans.Configuration;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FabrCore.Host
{

    public class FabrCoreServerOptions
    {
        public List<Assembly> AdditionalAssemblies { get; set; } = new();

        /// <summary>
        /// The implementation type for <see cref="IAgentManagementProvider"/>.
        /// Defaults to <see cref="OrleansAgentManagementProvider"/> which delegates to
        /// the <c>AgentManagementGrain</c> Orleans grain.
        /// </summary>
        internal Type AgentManagementProviderType { get; private set; } = typeof(OrleansAgentManagementProvider);

        /// <summary>
        /// The implementation type for <see cref="IAclProvider"/>.
        /// Defaults to <see cref="InMemoryAclProvider"/> which loads rules from configuration.
        /// </summary>
        internal Type AclProviderType { get; private set; } = typeof(InMemoryAclProvider);

        /// <summary>
        /// The implementation type for <see cref="IAgentMessageMonitor"/>.
        /// Null by default (monitoring disabled). Call <see cref="UseAgentMessageMonitor{T}"/>
        /// or <see cref="UseInMemoryAgentMessageMonitor"/> to enable.
        /// </summary>
        internal Type? AgentMessageMonitorType { get; private set; }

        /// <summary>
        /// Options controlling LLM call capture behavior. Registered as a singleton and consumed
        /// by <see cref="InMemoryAgentMessageMonitor"/> and <c>TokenTrackingChatClient</c>.
        /// </summary>
        internal LlmCaptureOptions LlmCaptureOptions { get; } = new LlmCaptureOptions();

        /// <summary>
        /// Configures a custom <see cref="IAgentManagementProvider"/> implementation for
        /// agent/client registration, tracking, and lifecycle management.
        /// </summary>
        /// <typeparam name="T">The provider implementation type.</typeparam>
        public FabrCoreServerOptions UseAgentManagementProvider<T>() where T : class, IAgentManagementProvider
        {
            AgentManagementProviderType = typeof(T);
            return this;
        }

        /// <summary>
        /// Configures a custom <see cref="IAclProvider"/> implementation for
        /// agent access control. The default is <see cref="InMemoryAclProvider"/>.
        /// </summary>
        /// <typeparam name="T">The provider implementation type.</typeparam>
        public FabrCoreServerOptions UseAclProvider<T>() where T : class, IAclProvider
        {
            AclProviderType = typeof(T);
            return this;
        }

        /// <summary>
        /// Configures a custom <see cref="IAgentMessageMonitor"/> implementation for
        /// agent message monitoring.
        /// </summary>
        /// <typeparam name="T">The monitor implementation type.</typeparam>
        public FabrCoreServerOptions UseAgentMessageMonitor<T>() where T : class, IAgentMessageMonitor
        {
            AgentMessageMonitorType = typeof(T);
            return this;
        }

        /// <summary>
        /// Enables agent message monitoring with the built-in <see cref="InMemoryAgentMessageMonitor"/>.
        /// Messages are stored in a bounded FIFO buffer (default 5000) with accumulated token tracking.
        /// </summary>
        /// <param name="configureLlmCapture">
        /// Optional callback to configure LLM call capture. By default only metadata
        /// (model, tokens, duration, finish reason) is captured. Set
        /// <see cref="LlmCaptureOptions.CapturePayloads"/> to true to also capture prompts and
        /// responses — be sure to configure <see cref="LlmCaptureOptions.Redact"/> when doing so,
        /// as prompts may contain PII or secrets.
        /// </param>
        public FabrCoreServerOptions UseInMemoryAgentMessageMonitor(Action<LlmCaptureOptions>? configureLlmCapture = null)
        {
            AgentMessageMonitorType = typeof(InMemoryAgentMessageMonitor);
            configureLlmCapture?.Invoke(LlmCaptureOptions);
            return this;
        }
    }

    /// <summary>
    /// Obsolete alias for FabrCoreServerOptions. Use FabrCoreServerOptions instead.
    /// </summary>
    [Obsolete("Use FabrCoreServerOptions instead.")]
    public class StandaloneServerOptions : FabrCoreServerOptions { }

    public static class FabrCoreHostExtensions
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.Extensions");
        private static readonly Meter Meter = new("FabrCore.Host.Extensions");
        private static int _fabrCoreListenerRegistered;

        /// <summary>
        /// Registers a process-wide <see cref="ActivityListener"/> for all FabrCore.* ActivitySources
        /// so that <c>ActivitySource.StartActivity</c> calls actually materialize <see cref="Activity"/>
        /// instances (and therefore populate <c>Activity.Current.TraceId</c> / <c>SpanId</c>). Without this,
        /// ingress-point trace stamping is a no-op. Consumers can still add their own OpenTelemetry
        /// TracerProvider with exporters on top — multiple listeners coexist safely.
        /// </summary>
        private static void EnsureFabrCoreActivityListenerRegistered()
        {
            if (Interlocked.Exchange(ref _fabrCoreListenerRegistered, 1) == 1) return;

            ActivitySource.AddActivityListener(new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("FabrCore.", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            });
        }

        // Metrics
        private static readonly Counter<long> ServerConfiguredCounter = Meter.CreateCounter<long>(
            "fabrcore.host.server.configured",
            description: "Number of standalone servers configured");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.host.errors",
            description: "Number of errors encountered in host extensions");

        public static WebApplication UseFabrCoreServer(this WebApplication app, FabrCoreServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("UseFabrCoreServer", ActivityKind.Internal);
            var logger = app.Services.GetRequiredService<ILogger<WebApplication>>();

            activity?.SetTag("options.has_additional_assemblies", options?.AdditionalAssemblies?.Count > 0);
            activity?.SetTag("options.assembly_count", options?.AdditionalAssemblies?.Count ?? 0);

            logger.LogInformation("Configuring FabrCore server");
            logger.LogDebug("Additional assemblies count: {AssemblyCount}", options?.AdditionalAssemblies?.Count ?? 0);

            try
            {
                // Map FabrCore API controllers
                app.MapControllers();
                logger.LogInformation("FabrCore API controllers mapped");

                // Map health endpoints for Kubernetes-style probes.
                // /health/live: process is alive (no tagged checks required).
                // /health/ready: Orleans cluster client is ready (tagged "ready").
                // /health: detailed JSON served by HealthController (registered above).
                app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = _ => false // no checks run — just a liveness ping
                });
                app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("ready")
                });
                logger.LogInformation("Health endpoints mapped (/health, /health/live, /health/ready)");

                // Enable WebSocket support with configured keep-alive interval.
                var hostOptions = app.Services
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.FabrCoreHostOptions>>()
                    .Value;
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = hostOptions.WebSocketKeepAliveInterval,
                });
                logger.LogInformation(
                    "WebSocket support enabled (keep-alive {KeepAlive}, path {Path})",
                    hostOptions.WebSocketKeepAliveInterval, hostOptions.WebSocketPath);

                // Add WebSocket middleware (path resolved from FabrCoreHostOptions).
                app.UseMiddleware<FabrCore.Host.WebSocket.WebSocketMiddleware>();
                logger.LogInformation("WebSocket middleware added");

                logger.LogInformation("FabrCore server configured successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return app;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error configuring FabrCore server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_configuration_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for UseFabrCoreServer. Use UseFabrCoreServer instead.
        /// </summary>
        [Obsolete("Use UseFabrCoreServer instead.")]
        public static WebApplication UseFabrCoreStandaloneServer(this WebApplication app, FabrCoreServerOptions? options = null)
            => UseFabrCoreServer(app, options);

        /// <summary>
        /// Registers FabrCore's non-Orleans services (DI, controllers, background services).
        /// <para>
        /// Use this when you are calling <c>builder.UseOrleans()</c> yourself and using
        /// <see cref="FabrCoreSiloBuilderExtensions.AddFabrCore"/> for the Orleans-specific parts.
        /// </para>
        /// </summary>
        public static WebApplicationBuilder AddFabrCoreServices(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
        {
            EnsureFabrCoreActivityListenerRegistered();

            using var activity = ActivitySource.StartActivity("AddFabrCoreServices", ActivityKind.Internal);
            options ??= new FabrCoreServerOptions();

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("FabrCore.Host.Extensions");

            logger.LogInformation("Adding FabrCore services - Environment: {Environment}", builder.Environment.EnvironmentName);

            try
            {
                builder.Services.AddHttpContextAccessor();
                logger.LogDebug("HttpContextAccessor added");

                builder.Services.AddControllers()
                    .AddApplicationPart(typeof(FabrCoreServerOptions).Assembly);
                logger.LogDebug("FabrCore API controllers registered");

                builder.Services.AddSingleton<FabrCore.Sdk.IFabrCoreChatClientService, FabrCore.Sdk.FabrCoreChatClientService>();
                logger.LogDebug("FabrCoreChatClientService added");

                builder.Services.AddSingleton<FabrCore.Sdk.FabrCoreToolRegistry>();
                logger.LogDebug("FabrCoreToolRegistry added");

                builder.Services.AddSingleton<FabrCore.Sdk.IFabrCoreRegistry, FabrCore.Sdk.FabrCoreRegistry>();
                logger.LogDebug("FabrCoreRegistry added");

                // Configure Embeddings
                builder.Services.AddSingleton<IEmbeddings, Embeddings>();

                // Configure File Storage
                builder.Services.Configure<FileStorageSettings>(builder.Configuration.GetSection("FileStorage"));
                builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
                builder.Services.AddHostedService<FileCleanupBackgroundService>();
                logger.LogInformation("File storage services configured");

                // Configure Compaction
                builder.Services.AddSingleton<FabrCore.Sdk.CompactionService>();
                logger.LogDebug("CompactionService added");

                // Configure Agent Management Provider (pluggable — default is Orleans grain-backed)
                builder.Services.AddSingleton(typeof(IAgentManagementProvider), options.AgentManagementProviderType);
                logger.LogDebug("AgentManagementProvider added: {ProviderType}", options.AgentManagementProviderType.Name);

                // Configure ACL Provider (pluggable — default is in-memory from config)
                builder.Services.Configure<FabrCoreAclOptions>(builder.Configuration.GetSection("FabrCore:Acl"));
                builder.Services.AddSingleton(typeof(IAclProvider), options.AclProviderType);
                logger.LogDebug("AclProvider added: {ProviderType}", options.AclProviderType.Name);

                // Bind tunable options (see FabrCoreHostOptions / AgentGrainOptions / ClientGrainOptions).
                builder.Services.Configure<Configuration.FabrCoreHostOptions>(
                    builder.Configuration.GetSection(Configuration.FabrCoreHostOptions.SectionName));
                builder.Services.Configure<Configuration.AgentGrainOptions>(
                    builder.Configuration.GetSection(Configuration.AgentGrainOptions.SectionName));
                builder.Services.Configure<Configuration.ClientGrainOptions>(
                    builder.Configuration.GetSection(Configuration.ClientGrainOptions.SectionName));

                // Pluggable WebSocket authenticator. Default preserves legacy header/query behavior;
                // production apps override via AddFabrCoreServices().Services.AddSingleton<IWebSocketAuthenticator, MyAuthN>().
                builder.Services.TryAddSingleton<WebSocket.IWebSocketAuthenticator, WebSocket.DefaultWebSocketAuthenticator>();

                // Token cost calculator — default reads FabrCore:ModelPricing from config. Hosts
                // can register their own ITokenCostCalculator for dynamic pricing.
                builder.Services.TryAddSingleton<FabrCore.Core.Monitoring.ITokenCostCalculator, Services.ConfigurableTokenCostCalculator>();

                logger.LogDebug("FabrCore runtime options bound");

                // Configure Agent Message Monitor (opt-in — disabled by default)
                // Register LlmCaptureOptions as a singleton so the monitor and
                // TokenTrackingChatClient can pick it up via DI.
                builder.Services.AddSingleton(options.LlmCaptureOptions);

                if (options.AgentMessageMonitorType is not null)
                {
                    builder.Services.AddSingleton(typeof(IAgentMessageMonitor), options.AgentMessageMonitorType);
                    logger.LogInformation("AgentMessageMonitor enabled: {MonitorType}", options.AgentMessageMonitorType.Name);
                }
                else
                {
                    builder.Services.AddSingleton<IAgentMessageMonitor, NullAgentMessageMonitor>();
                    logger.LogDebug("AgentMessageMonitor not configured — monitoring disabled");
                }

                // Configure Agent Service
                builder.Services.AddSingleton<IFabrCoreAgentService, FabrCoreAgentService>();
                logger.LogDebug("FabrCoreAgentService added");

                // Configure Agent Registry Cleanup
                builder.Services.AddHostedService<AgentRegistryCleanupService>();
                logger.LogInformation("Agent registry cleanup service configured");

                // Configure Health Checks (wired to endpoints in UseFabrCoreServer).
                // "live" = process is up; "ready" = Orleans cluster client usable.
                builder.Services.AddHealthChecks()
                    .AddCheck<Services.OrleansClusterHealthCheck>(
                        "orleans-cluster",
                        tags: new[] { "ready" });
                logger.LogDebug("Health checks registered");

                logger.LogInformation("FabrCore services added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding FabrCore services");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "services_add_failed"));
                throw;
            }
        }

        /// <summary>
        /// Configures a complete FabrCore server with Orleans silo using <see cref="OrleansClusterOptions"/> from configuration.
        /// <para>
        /// This is the simple path — it calls <see cref="AddFabrCoreServices"/> and configures Orleans internally.
        /// For full Orleans control, use <see cref="AddFabrCoreServices"/> + <c>builder.UseOrleans()</c> +
        /// <see cref="FabrCoreSiloBuilderExtensions.AddFabrCore"/> instead.
        /// </para>
        /// </summary>
        public static WebApplicationBuilder AddFabrCoreServer(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("AddFabrCoreServer", ActivityKind.Internal);
            options ??= new FabrCoreServerOptions();

            activity?.SetTag("options.assembly_count", options.AdditionalAssemblies.Count);
            activity?.SetTag("environment", builder.Environment.EnvironmentName);

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("FabrCore.Host.Extensions");

            logger.LogInformation("Adding FabrCore server - Environment: {Environment}", builder.Environment.EnvironmentName);
            logger.LogDebug("Additional assemblies to load: {AssemblyCount}", options.AdditionalAssemblies.Count);

            try
            {
                // Register non-Orleans services
                builder.AddFabrCoreServices(options);

                // Load Orleans cluster options from configuration
                var orleansOptions = builder.Configuration
                    .GetSection(OrleansClusterOptions.SectionName)
                    .Get<OrleansClusterOptions>() ?? new OrleansClusterOptions();

                logger.LogInformation("Orleans clustering mode: {ClusteringMode}", orleansOptions.ClusteringMode);
                activity?.SetTag("orleans.clustering_mode", orleansOptions.ClusteringMode.ToString());

                // Auto-initialize Orleans SQL Server tables if needed
                if (orleansOptions.ClusteringMode == ClusteringMode.SqlServer && orleansOptions.AutoInitDatabase)
                {
                    logger.LogInformation("Auto-initializing Orleans SQL Server database tables");
                    Services.OrleansSqlServerInitializer.EnsureOrleansTablesExist(orleansOptions, logger);
                }

                builder.UseOrleans(siloBuilder =>
                {
                    using var orleansActivity = ActivitySource.StartActivity("ConfigureOrleans", ActivityKind.Internal);
                    logger.LogInformation("Configuring Orleans silo");

                    // Configure clustering based on mode
                    ConfigureClustering(siloBuilder, orleansOptions, logger);

                    // Configure persistence based on mode
                    ConfigurePersistence(siloBuilder, orleansOptions, logger);

                    // Configure reminders based on mode
                    ConfigureReminders(siloBuilder, orleansOptions, logger);

                    // Streaming always uses memory streams (no SQL Server streaming provider exists)
                    siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
                    logger.LogDebug("Orleans memory streams configured");

                    logger.LogInformation("Orleans clustering, persistence, and reminders configured");

                    // Register FabrCore grain assemblies
                    siloBuilder.AddFabrCore(options.AdditionalAssemblies);

                    orleansActivity?.SetStatus(ActivityStatusCode.Ok);
                });

                ServerConfiguredCounter.Add(1,
                    new KeyValuePair<string, object?>("environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object?>("assemblies.count", options.AdditionalAssemblies.Count));

                logger.LogInformation("FabrCore server added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding FabrCore server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_add_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for AddFabrCoreServer. Use AddFabrCoreServer instead.
        /// </summary>
        [Obsolete("Use AddFabrCoreServer instead.")]
        public static WebApplicationBuilder AddFabrCoreStandaloneServer(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
            => AddFabrCoreServer(builder, options);

        /// <summary>
        /// Configures Orleans clustering based on the specified mode.
        /// </summary>
        private static void ConfigureClustering(ISiloBuilder siloBuilder, OrleansClusterOptions options, ILogger logger)
        {
            switch (options.ClusteringMode)
            {
                case ClusteringMode.SqlServer:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using SqlServer clustering mode.");
                    }

                    siloBuilder.UseAdoNetClustering(clustering =>
                    {
                        clustering.Invariant = "Microsoft.Data.SqlClient";
                        clustering.ConnectionString = options.ConnectionString;
                    });
                    siloBuilder.Configure<ClusterOptions>(cluster =>
                    {
                        cluster.ClusterId = options.ClusterId;
                        cluster.ServiceId = options.ServiceId;
                    });
                    logger.LogInformation("Orleans SQL Server clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
                        options.ClusterId, options.ServiceId);
                    break;

                case ClusteringMode.AzureStorage:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using AzureStorage clustering mode.");
                    }

                    siloBuilder.UseAzureStorageClustering(clustering =>
                    {
                        clustering.ConfigureTableServiceClient(options.ConnectionString);
                    });
                    siloBuilder.Configure<ClusterOptions>(cluster =>
                    {
                        cluster.ClusterId = options.ClusterId;
                        cluster.ServiceId = options.ServiceId;
                    });
                    logger.LogInformation("Orleans Azure Storage clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
                        options.ClusterId, options.ServiceId);
                    break;

                case ClusteringMode.Localhost:
                default:
                    siloBuilder.UseLocalhostClustering();
                    logger.LogDebug("Orleans localhost clustering configured");
                    break;
            }
        }

        /// <summary>
        /// Configures Orleans grain persistence based on the specified mode.
        /// </summary>
        private static void ConfigurePersistence(ISiloBuilder siloBuilder, OrleansClusterOptions options, ILogger logger)
        {
            var storageConnectionString = options.EffectiveStorageConnectionString;

            switch (options.ClusteringMode)
            {
                case ClusteringMode.SqlServer:
                    if (string.IsNullOrEmpty(storageConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString or Orleans:StorageConnectionString is required when using SqlServer clustering mode.");
                    }

                    siloBuilder.AddAdoNetGrainStorage(FabrCoreOrleansConstants.StorageProviderName, storage =>
                    {
                        storage.Invariant = "Microsoft.Data.SqlClient";
                        storage.ConnectionString = storageConnectionString;
                    });
                    siloBuilder.AddAdoNetGrainStorage(FabrCoreOrleansConstants.PubSubStoreName, storage =>
                    {
                        storage.Invariant = "Microsoft.Data.SqlClient";
                        storage.ConnectionString = storageConnectionString;
                    });
                    logger.LogInformation("Orleans SQL Server grain persistence configured");
                    break;

                case ClusteringMode.AzureStorage:
                    if (string.IsNullOrEmpty(storageConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString or Orleans:StorageConnectionString is required when using AzureStorage clustering mode.");
                    }

                    siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.StorageProviderName, storage =>
                    {
                        storage.ConfigureTableServiceClient(storageConnectionString);
                    });
                    siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.PubSubStoreName, storage =>
                    {
                        storage.ConfigureTableServiceClient(storageConnectionString);
                    });
                    logger.LogInformation("Orleans Azure Storage grain persistence configured");
                    break;

                case ClusteringMode.Localhost:
                default:
                    siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.StorageProviderName);
                    siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.PubSubStoreName);
                    logger.LogDebug("Orleans memory grain storage configured");
                    break;
            }
        }

        /// <summary>
        /// Configures Orleans reminders based on the specified mode.
        /// </summary>
        private static void ConfigureReminders(ISiloBuilder siloBuilder, OrleansClusterOptions options, ILogger logger)
        {
            switch (options.ClusteringMode)
            {
                case ClusteringMode.SqlServer:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using SqlServer clustering mode.");
                    }

                    siloBuilder.UseAdoNetReminderService(reminders =>
                    {
                        reminders.Invariant = "Microsoft.Data.SqlClient";
                        reminders.ConnectionString = options.ConnectionString;
                    });
                    logger.LogInformation("Orleans SQL Server reminders configured");
                    break;

                case ClusteringMode.AzureStorage:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using AzureStorage clustering mode.");
                    }

                    siloBuilder.UseAzureTableReminderService(reminders =>
                    {
                        reminders.ConfigureTableServiceClient(options.ConnectionString);
                    });
                    logger.LogInformation("Orleans Azure Storage reminders configured");
                    break;

                case ClusteringMode.Localhost:
                default:
                    siloBuilder.UseInMemoryReminderService();
                    logger.LogDebug("Orleans in-memory reminder service configured");
                    break;
            }
        }
    }
}
