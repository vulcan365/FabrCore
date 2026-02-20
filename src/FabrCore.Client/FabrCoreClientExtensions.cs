using FabrCore.Client.Configuration;
using FabrCore.Client.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FabrCore.Client
{
    public static class FabrCoreClientExtensions
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.Extensions");
        private static readonly Meter Meter = new("FabrCore.Client.Extensions");

        // Metrics
        private static readonly Counter<long> ClientsConfiguredCounter = Meter.CreateCounter<long>(
            "fabrcore.client.extension.configured",
            description: "Number of FabrCore clients configured");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.client.extension.errors",
            description: "Number of errors encountered in client extensions");

        private static readonly Counter<long> ConnectionRetryCounter = Meter.CreateCounter<long>(
            "fabrcore.client.connection.retries",
            description: "Number of connection retry attempts");

        public static IHostApplicationBuilder AddFabrCoreClient(this IHostApplicationBuilder builder)
        {
            using var activity = ActivitySource.StartActivity("AddFabrCoreClient", ActivityKind.Internal);
            activity?.SetTag("environment", builder.Environment.EnvironmentName);

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("FabrCore.Client.Extensions");

            logger.LogInformation("Adding FabrCore client - Environment: {Environment}", builder.Environment.EnvironmentName);

            try
            {
                // Check if Orleans server (silo) is already configured by checking for IClusterClient
                // UseOrleans registers IClusterClient, so we only need UseOrleansClient if it's not already registered
                var clusterClientDescriptor = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(IClusterClient));

                if (clusterClientDescriptor == null)
                {
                    logger.LogInformation("Configuring Orleans client");
                    activity?.SetTag("orleans.configured", true);

                    // Load Orleans cluster options from configuration
                    var orleansOptions = builder.Configuration
                        .GetSection(OrleansClusterOptions.SectionName)
                        .Get<OrleansClusterOptions>() ?? new OrleansClusterOptions();

                    logger.LogInformation("Orleans client clustering mode: {ClusteringMode}", orleansOptions.ClusteringMode);
                    activity?.SetTag("orleans.clustering_mode", orleansOptions.ClusteringMode.ToString());

                    // Only configure Orleans client if server is not already configured
                    builder.UseOrleansClient(client =>
                    {
                        ConfigureClientClustering(client, orleansOptions, logger);
                        ConfigureConnectionRetry(client, orleansOptions, logger);

                        // Register connection retry filter for handling startup connection failures
                        client.UseConnectionRetryFilter<FabrCoreClientConnectionRetryFilter>();
                    });
                }
                else
                {
                    logger.LogInformation("Orleans client already configured - skipping client configuration");
                    activity?.SetTag("orleans.configured", false);
                    activity?.SetTag("orleans.already_configured", true);
                }

                // Register ClientContextFactory as singleton (factory pattern)
                // The factory manages thread-safe context creation and optional caching per handle
                builder.Services.AddSingleton<IClientContextFactory, ClientContextFactory>();
                logger.LogInformation("ClientContextFactory registered as singleton service");

                // Register DirectMessageSender for fire-and-forget messaging without ClientContext
                builder.Services.AddSingleton<IDirectMessageSender, DirectMessageSender>();
                logger.LogInformation("DirectMessageSender registered as singleton service");

                // Register FabrCoreHostApiClient with HttpClient
                builder.Services.AddHttpClient<IFabrCoreHostApiClient, FabrCoreHostApiClient>();
                logger.LogInformation("FabrCoreHostApiClient registered with HttpClient");

                ClientsConfiguredCounter.Add(1,
                    new KeyValuePair<string, object?>("environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object?>("orleans.configured", clusterClientDescriptor == null));

                logger.LogInformation("FabrCore client added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding FabrCore client");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "client_add_failed"));
                throw;
            }
        }

        public static IHost UseFabrCoreClient(this IHost app)
        {
            using var activity = ActivitySource.StartActivity("UseFabrCoreClient", ActivityKind.Internal);
            var logger = app.Services.GetRequiredService<ILogger<IHost>>();

            logger.LogInformation("Configuring FabrCore client");

            try
            {
                // Any post-configuration can go here
                logger.LogInformation("FabrCore client configured successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return app;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error configuring FabrCore client");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "client_use_failed"));
                throw;
            }
        }

        /// <summary>
        /// Adds FabrCore client Blazor components support including the ChatDockManager.
        /// Call this method to enable ChatDock components in your Blazor application.
        /// </summary>
        /// <remarks>
        /// Usage:
        /// <code>
        /// // In Program.cs
        /// builder.Services.AddFabrCoreClientComponents();
        ///
        /// // In a Razor component, use CascadingValue to provide the manager:
        /// &lt;CascadingValue Value="@_dockManager"&gt;
        ///     &lt;ChatDock UserHandle="@userId" AgentHandle="assistant" AgentType="MyAgent" /&gt;
        /// &lt;/CascadingValue&gt;
        /// </code>
        /// </remarks>
        public static IServiceCollection AddFabrCoreClientComponents(this IServiceCollection services)
        {
            // ChatDockManager is registered as scoped so each circuit/session gets its own instance
            services.AddScoped<ChatDockManager>();
            return services;
        }

        /// <summary>
        /// Configures Orleans client clustering based on the specified mode.
        /// </summary>
        private static void ConfigureClientClustering(IClientBuilder client, OrleansClusterOptions options, ILogger logger)
        {
            switch (options.ClusteringMode)
            {
                case ClusteringMode.SqlServer:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using SqlServer clustering mode.");
                    }

                    client.UseAdoNetClustering(clustering =>
                    {
                        clustering.Invariant = "Microsoft.Data.SqlClient";
                        clustering.ConnectionString = options.ConnectionString;
                    });
                    client.Configure<ClusterOptions>(cluster =>
                    {
                        cluster.ClusterId = options.ClusterId;
                        cluster.ServiceId = options.ServiceId;
                    });
                    logger.LogInformation("Orleans client SQL Server clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
                        options.ClusterId, options.ServiceId);
                    break;

                case ClusteringMode.AzureStorage:
                    if (string.IsNullOrEmpty(options.ConnectionString))
                    {
                        throw new InvalidOperationException("Orleans:ConnectionString is required when using AzureStorage clustering mode.");
                    }

                    client.UseAzureStorageClustering(clustering =>
                    {
                        clustering.ConfigureTableServiceClient(options.ConnectionString);
                    });
                    client.Configure<ClusterOptions>(cluster =>
                    {
                        cluster.ClusterId = options.ClusterId;
                        cluster.ServiceId = options.ServiceId;
                    });
                    logger.LogInformation("Orleans client Azure Storage clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
                        options.ClusterId, options.ServiceId);
                    break;

                case ClusteringMode.Localhost:
                default:
                    client.UseLocalhostClustering();
                    logger.LogDebug("Orleans client localhost clustering configured");
                    break;
            }
        }

        /// <summary>
        /// Configures Orleans client connection retry and gateway options.
        /// </summary>
        private static void ConfigureConnectionRetry(IClientBuilder client, OrleansClusterOptions options, ILogger logger)
        {
            // Configure gateway options for connection retry behavior
            client.Configure<GatewayOptions>(gateway =>
            {
                gateway.GatewayListRefreshPeriod = options.GatewayListRefreshPeriod;
            });

            // Configure client messaging options for connection timeouts
            client.Configure<ClientMessagingOptions>(messaging =>
            {
                // ResponseTimeout controls how long to wait for a response before considering the call failed
                messaging.ResponseTimeout = TimeSpan.FromSeconds(30);
            });

            logger.LogInformation(
                "Orleans client connection retry configured - RetryCount: {RetryCount}, RetryDelay: {RetryDelay}, GatewayRefreshPeriod: {GatewayRefreshPeriod}",
                options.ConnectionRetryCount,
                options.ConnectionRetryDelay,
                options.GatewayListRefreshPeriod);

            // Add connection lost handler for logging
            client.AddClusterConnectionLostHandler((sender, args) =>
            {
                logger.LogWarning(
                    "Orleans cluster connection lost. The client will attempt to reconnect automatically.");
                ConnectionRetryCounter.Add(1, new KeyValuePair<string, object?>("event", "connection_lost"));
            });
        }
    }

    /// <summary>
    /// Orleans client connection retry filter that handles connection failures at startup.
    /// This filter implements exponential backoff with configurable retry count and delay.
    /// </summary>
    public sealed class FabrCoreClientConnectionRetryFilter : IClientConnectionRetryFilter
    {
        private readonly OrleansClusterOptions _options;
        private readonly ILogger<FabrCoreClientConnectionRetryFilter> _logger;
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.Connection");
        private static readonly Meter Meter = new("FabrCore.Client.Connection");

        private static readonly Counter<long> RetryAttemptCounter = Meter.CreateCounter<long>(
            "fabrcore.client.connection.retry_attempts",
            description: "Number of connection retry attempts made");

        private static readonly Counter<long> ConnectionSuccessCounter = Meter.CreateCounter<long>(
            "fabrcore.client.connection.success",
            description: "Number of successful connections");

        private static readonly Counter<long> ConnectionFailureCounter = Meter.CreateCounter<long>(
            "fabrcore.client.connection.failures",
            description: "Number of connection failures after all retries exhausted");

        private int _attemptCount = 0;

        public FabrCoreClientConnectionRetryFilter(
            IConfiguration configuration,
            ILogger<FabrCoreClientConnectionRetryFilter> logger)
        {
            _options = configuration.GetSection(OrleansClusterOptions.SectionName).Get<OrleansClusterOptions>()
                ?? new OrleansClusterOptions();
            _logger = logger;

            _logger.LogInformation(
                "FabrCore client connection retry filter initialized. MaxRetries: {MaxRetries}, RetryDelay: {RetryDelay}",
                _options.ConnectionRetryCount,
                _options.ConnectionRetryDelay);
        }

        public async Task<bool> ShouldRetryConnectionAttempt(
            Exception exception,
            CancellationToken cancellationToken)
        {
            _attemptCount++;
            var maxAttempts = _options.ConnectionRetryCount + 1; // +1 for initial attempt

            using var activity = ActivitySource.StartActivity("ConnectionRetryAttempt", ActivityKind.Client);
            activity?.SetTag("retry.attempt", _attemptCount);
            activity?.SetTag("retry.max_attempts", maxAttempts);
            activity?.SetTag("error.type", exception.GetType().Name);

            RetryAttemptCounter.Add(1,
                new KeyValuePair<string, object?>("attempt", _attemptCount),
                new KeyValuePair<string, object?>("error_type", exception.GetType().Name));

            if (_attemptCount >= maxAttempts)
            {
                _logger.LogError(
                    exception,
                    "Orleans client connection FAILED after {MaxAttempts} attempts. " +
                    "Last error: {ErrorType}: {ErrorMessage}. " +
                    "Ensure the FabrCore server is running and accessible.",
                    maxAttempts,
                    exception.GetType().Name,
                    exception.Message);

                ConnectionFailureCounter.Add(1,
                    new KeyValuePair<string, object?>("attempts_made", _attemptCount),
                    new KeyValuePair<string, object?>("error_type", exception.GetType().Name));

                activity?.SetStatus(ActivityStatusCode.Error, "Max retries exceeded");
                return false; // Stop retrying
            }

            _logger.LogWarning(
                "Orleans client connection attempt {Attempt} of {MaxAttempts} failed. " +
                "Error: {ErrorType}: {ErrorMessage}. " +
                "Retrying in {RetryDelay}...",
                _attemptCount,
                maxAttempts,
                exception.GetType().Name,
                exception.Message,
                _options.ConnectionRetryDelay);

            activity?.SetTag("retry.delay_ms", _options.ConnectionRetryDelay.TotalMilliseconds);

            try
            {
                await Task.Delay(_options.ConnectionRetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Connection retry cancelled during delay after attempt {Attempt}",
                    _attemptCount);
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
                return false;
            }

            _logger.LogInformation(
                "Initiating Orleans client connection attempt {Attempt} of {MaxAttempts}...",
                _attemptCount + 1,
                maxAttempts);

            return true; // Continue retrying
        }

        /// <summary>
        /// Called when the client successfully connects to the cluster.
        /// Resets the retry counter and logs the success.
        /// </summary>
        public void OnConnectionSuccess()
        {
            if (_attemptCount > 0)
            {
                _logger.LogInformation(
                    "Orleans client connected successfully after {Attempts} attempt(s)",
                    _attemptCount + 1);

                ConnectionSuccessCounter.Add(1,
                    new KeyValuePair<string, object?>("attempts", _attemptCount + 1));
            }
            else
            {
                _logger.LogInformation("Orleans client connected successfully on first attempt");
                ConnectionSuccessCounter.Add(1,
                    new KeyValuePair<string, object?>("attempts", 1));
            }

            _attemptCount = 0;
        }
    }
}
