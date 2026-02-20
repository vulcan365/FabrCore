using Fabr.Core.Streaming;
using Fabr.Host.Configuration;
using Fabr.Host.Services;
using Fabr.Sdk;
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

namespace Fabr.Host
{

    public class FabrServerOptions
    {
        public List<Assembly> AdditionalAssemblies { get; set; } = new();
    }

    /// <summary>
    /// Obsolete alias for FabrServerOptions. Use FabrServerOptions instead.
    /// </summary>
    [Obsolete("Use FabrServerOptions instead.")]
    public class StandaloneServerOptions : FabrServerOptions { }

    public static class FabrHostExtensions
    {
        private static readonly ActivitySource ActivitySource = new("Fabr.Host.Extensions");
        private static readonly Meter Meter = new("Fabr.Host.Extensions");

        // Metrics
        private static readonly Counter<long> ServerConfiguredCounter = Meter.CreateCounter<long>(
            "fabr.host.server.configured",
            description: "Number of standalone servers configured");

        private static readonly Counter<long> AssembliesLoadedCounter = Meter.CreateCounter<long>(
            "fabr.host.assemblies.loaded",
            description: "Number of additional assemblies loaded");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabr.host.errors",
            description: "Number of errors encountered in host extensions");

        public static WebApplication UseFabrServer(this WebApplication app, FabrServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("UseFabrServer", ActivityKind.Internal);
            var logger = app.Services.GetRequiredService<ILogger<WebApplication>>();

            activity?.SetTag("options.has_additional_assemblies", options?.AdditionalAssemblies?.Count > 0);
            activity?.SetTag("options.assembly_count", options?.AdditionalAssemblies?.Count ?? 0);

            logger.LogInformation("Configuring Fabr server");
            logger.LogDebug("Additional assemblies count: {AssemblyCount}", options?.AdditionalAssemblies?.Count ?? 0);

            try
            {
                // Map Fabr API controllers
                app.MapControllers();
                logger.LogInformation("Fabr API controllers mapped");

                // Enable WebSocket support with specific options
                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                app.UseWebSockets(webSocketOptions);
                logger.LogInformation("WebSocket support enabled");

                // Add WebSocket middleware - this will only handle /ws path
                app.UseMiddleware<Fabr.Host.WebSocket.WebSocketMiddleware>();
                logger.LogInformation("WebSocket middleware added");

                logger.LogInformation("Fabr server configured successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return app;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error configuring Fabr server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_configuration_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for UseFabrServer. Use UseFabrServer instead.
        /// </summary>
        [Obsolete("Use UseFabrServer instead.")]
        public static WebApplication UseFabrStandaloneServer(this WebApplication app, FabrServerOptions? options = null)
            => UseFabrServer(app, options);

        public static WebApplicationBuilder AddFabrServer(this WebApplicationBuilder builder, FabrServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("AddFabrServer", ActivityKind.Internal);
            options ??= new FabrServerOptions();

            activity?.SetTag("options.assembly_count", options.AdditionalAssemblies.Count);
            activity?.SetTag("environment", builder.Environment.EnvironmentName);

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("Fabr.Host.Extensions");

            logger.LogInformation("Adding Fabr server - Environment: {Environment}", builder.Environment.EnvironmentName);
            logger.LogDebug("Additional assemblies to load: {AssemblyCount}", options.AdditionalAssemblies.Count);

            try
            {
                builder.Services.AddHttpContextAccessor();
                logger.LogDebug("HttpContextAccessor added");

                builder.Services.AddControllers()
                    .AddApplicationPart(typeof(FabrServerOptions).Assembly);
                logger.LogDebug("Fabr API controllers registered");

                builder.Services.AddSingleton<Fabr.Sdk.IFabrChatClientService, Fabr.Sdk.FabrChatClientService>();
                logger.LogDebug("FabrChatClientService added");

                builder.Services.AddSingleton<Fabr.Sdk.FabrToolRegistry>();
                logger.LogDebug("FabrToolRegistry added");

                builder.Services.AddSingleton<Fabr.Sdk.IFabrRegistry, Fabr.Sdk.FabrRegistry>();
                logger.LogDebug("FabrRegistry added");

                // Configure Embeddings
                builder.Services.AddTransient<IEmbeddings, Embeddings>();

                // Configure File Storage
                builder.Services.Configure<FileStorageSettings>(builder.Configuration.GetSection("FileStorage"));
                builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
                builder.Services.AddHostedService<FileCleanupBackgroundService>();
                logger.LogInformation("File storage services configured");

                // Configure Agent Registry Cleanup
                builder.Services.AddHostedService<AgentRegistryCleanupService>();
                logger.LogInformation("Agent registry cleanup service configured");

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
                    siloBuilder.AddMemoryStreams(StreamConstants.ProviderName);
                    logger.LogDebug("Orleans memory streams configured");

                    logger.LogInformation("Orleans clustering, persistence, and reminders configured");

                    // Add additional assemblies to Orleans
                    var loadedCount = 0;
                    foreach (var assembly in options.AdditionalAssemblies)
                    {
                        using var assemblyActivity = ActivitySource.StartActivity("LoadAssembly", ActivityKind.Internal);
                        assemblyActivity?.SetTag("assembly.name", assembly.GetName().Name);

                        logger.LogInformation("Loading assembly: {AssemblyName}", assembly.GetName().Name);

                        try
                        {
                            //load assembly into .net
                            Assembly.Load(assembly.GetName().Name!);
                            loadedCount++;

                            AssembliesLoadedCounter.Add(1,
                                new KeyValuePair<string, object?>("assembly.name", assembly.GetName().Name));

                            logger.LogInformation("Assembly loaded successfully: {AssemblyName}", assembly.GetName().Name);
                            assemblyActivity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to load assembly: {AssemblyName}", assembly.GetName().Name);
                            assemblyActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                            assemblyActivity?.AddException(ex);
                            ErrorCounter.Add(1,
                                new KeyValuePair<string, object?>("error.type", "assembly_load_failed"),
                                new KeyValuePair<string, object?>("assembly.name", assembly.GetName().Name));
                            throw;
                        }
                    }

                    logger.LogInformation("Orleans configuration completed - Assemblies loaded: {LoadedCount}", loadedCount);
                    orleansActivity?.SetTag("assemblies.loaded", loadedCount);
                    orleansActivity?.SetStatus(ActivityStatusCode.Ok);
                });

                ServerConfiguredCounter.Add(1,
                    new KeyValuePair<string, object?>("environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object?>("assemblies.count", options.AdditionalAssemblies.Count));

                logger.LogInformation("Fabr server added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding Fabr server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_add_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for AddFabrServer. Use AddFabrServer instead.
        /// </summary>
        [Obsolete("Use AddFabrServer instead.")]
        public static WebApplicationBuilder AddFabrStandaloneServer(this WebApplicationBuilder builder, FabrServerOptions? options = null)
            => AddFabrServer(builder, options);

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

                    siloBuilder.AddAdoNetGrainStorage("fabrStorage", storage =>
                    {
                        storage.Invariant = "Microsoft.Data.SqlClient";
                        storage.ConnectionString = storageConnectionString;
                    });
                    siloBuilder.AddAdoNetGrainStorage("PubSubStore", storage =>
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

                    siloBuilder.AddAzureTableGrainStorage("fabrStorage", storage =>
                    {
                        storage.ConfigureTableServiceClient(storageConnectionString);
                    });
                    siloBuilder.AddAzureTableGrainStorage("PubSubStore", storage =>
                    {
                        storage.ConfigureTableServiceClient(storageConnectionString);
                    });
                    logger.LogInformation("Orleans Azure Storage grain persistence configured");
                    break;

                case ClusteringMode.Localhost:
                default:
                    siloBuilder.AddMemoryGrainStorage("fabrStorage");
                    siloBuilder.AddMemoryGrainStorage("PubSubStore");
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
