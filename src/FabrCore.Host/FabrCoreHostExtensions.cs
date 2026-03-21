using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

                // Enable WebSocket support with specific options
                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                app.UseWebSockets(webSocketOptions);
                logger.LogInformation("WebSocket support enabled");

                // Add WebSocket middleware - this will only handle /ws path
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

                // Configure Agent Service
                builder.Services.AddSingleton<IFabrCoreAgentService, FabrCoreAgentService>();
                logger.LogDebug("FabrCoreAgentService added");

                // Configure Agent Registry Cleanup
                builder.Services.AddHostedService<AgentRegistryCleanupService>();
                logger.LogInformation("Agent registry cleanup service configured");

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
