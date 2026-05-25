using FabrCore.Surface.Configuration;
using FabrCore.Surface.Rendering;
using FabrCore.Surface.Services;
using FabrCore.Surface.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;

namespace FabrCore.Surface;

public static class FabrCoreSurfaceExtensions
{
    public static IHostApplicationBuilder AddFabrCoreSurfaceFromConfig(
        this IHostApplicationBuilder builder,
        string definitionFilePath = "fabrcore-surface.json",
        string? definitionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionFilePath);

        var resolvedDefinitionName = string.IsNullOrWhiteSpace(definitionName) ? "default" : definitionName!;
        var definition = SurfaceDefinitionFileLoader.LoadByName(definitionFilePath, resolvedDefinitionName);

        builder.Services.AddFabrCoreSurfaceServices(options =>
        {
            options.DefinitionFilePath = definitionFilePath;
            options.DefaultSurfaceDefinitionName = resolvedDefinitionName;
            if (!string.IsNullOrWhiteSpace(definition?.PlanningModelName))
            {
                options.DefaultPlanningModelName = definition.PlanningModelName;
            }
        });

        builder.AddFabrCoreSurface(options =>
        {
            options.DefinitionFilePath = definitionFilePath;
            options.DefaultSurfaceDefinitionName = resolvedDefinitionName;
            if (!string.IsNullOrWhiteSpace(definition?.PlanningModelName))
            {
                options.DefaultPlanningModelName = definition.PlanningModelName;
            }

            if (definition is not null)
            {
                SurfaceDefinitionPolicyMapper.ApplyTo(definition, options);
            }
        });

        return builder;
    }

    public static IHostApplicationBuilder AddFabrCoreSurface(
        this IHostApplicationBuilder builder,
        Action<SurfaceOptions>? configure = null)
    {
        var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var logger = loggerFactory.CreateLogger("FabrCore.Surface.Extensions");

        var clusterClientDescriptor = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(IClusterClient));
        if (clusterClientDescriptor == null)
        {
            var orleansOptions = builder.Configuration
                .GetSection(OrleansClusterOptions.SectionName)
                .Get<OrleansClusterOptions>() ?? new OrleansClusterOptions();

            builder.UseOrleansClient(client =>
            {
                ConfigureClientClustering(client, orleansOptions, logger);
                ConfigureConnectionRetry(client, orleansOptions, logger);
                client.UseConnectionRetryFilter<SurfaceClientConnectionRetryFilter>();
            });
        }
        else
        {
            logger.LogInformation("Orleans client already configured; FabrCore.Surface will reuse it.");
        }

        var configuredOptions = new SurfaceOptions();
        if (configure != null)
        {
            configure(configuredOptions);
        }

        if (!string.IsNullOrWhiteSpace(configuredOptions.DefinitionFilePath))
        {
            var definitionName = string.IsNullOrWhiteSpace(configuredOptions.DefaultSurfaceDefinitionName)
                ? "default"
                : configuredOptions.DefaultSurfaceDefinitionName!;
            var definition = SurfaceDefinitionFileLoader.LoadByName(configuredOptions.DefinitionFilePath, definitionName);
            if (definition is not null)
            {
                SurfaceDefinitionPolicyMapper.ApplyTo(definition, configuredOptions);
                configuredOptions.DefaultPlanningModelName ??= definition.PlanningModelName;
            }

            builder.Services.AddFabrCoreSurfaceServices(options =>
            {
                options.DefinitionFilePath = configuredOptions.DefinitionFilePath;
                options.DefaultSurfaceDefinitionName = definitionName;
                options.DefaultPlanningModelName = configuredOptions.DefaultPlanningModelName;
            });
        }

        builder.Services.AddOptions<SurfaceOptions>();
        builder.Services.Configure<SurfaceOptions>(options => CopySurfaceOptions(configuredOptions, options));

        builder.Services.TryAddSingleton<ISurfaceClientContextFactory, SurfaceClientContextFactory>();
        builder.Services.TryAddSingleton<ISurfaceDirectMessageSender, SurfaceDirectMessageSender>();
        builder.Services.TryAddSingleton<ISurfaceActionRegistry, EmptySurfaceActionRegistry>();
        builder.Services.TryAddScoped<AdaptiveCardSurfaceValidator>();
        builder.Services.TryAddScoped<ISurfaceActionDispatcher, SurfaceActionDispatcher>();

        return builder;
    }

    public static IHost UseFabrCoreSurface(this IHost app)
        => app;

    public static IServiceCollection AddFabrCoreSurfaceComponents(this IServiceCollection services)
    {
        services.AddOptions<SurfaceOptions>();
        services.TryAddSingleton<ISurfaceActionRegistry, EmptySurfaceActionRegistry>();
        services.TryAddScoped<AdaptiveCardSurfaceValidator>();
        services.TryAddScoped<ISurfaceActionDispatcher, SurfaceActionDispatcher>();
        return services;
    }

    private static void CopySurfaceOptions(SurfaceOptions source, SurfaceOptions target)
    {
        target.DefinitionFilePath = source.DefinitionFilePath;
        target.DefaultSurfaceDefinitionName = source.DefaultSurfaceDefinitionName;
        target.DefaultPlanningModelName = source.DefaultPlanningModelName;
        target.MaxAdaptiveCardVersion = source.MaxAdaptiveCardVersion;
        target.MaxPayloadBytes = source.MaxPayloadBytes;
        target.MaxDepth = source.MaxDepth;
        target.AllowHttpUrls = source.AllowHttpUrls;
        target.AllowAnyActionVerb = source.AllowAnyActionVerb;
        target.AllowUnknownTargetAgents = source.AllowUnknownTargetAgents;
        target.EnableDiagnostics = source.EnableDiagnostics;

        ReplaceSet(target.AllowedActionTypes, source.AllowedActionTypes);
        ReplaceSet(target.AllowedActionVerbs, source.AllowedActionVerbs);
        ReplaceSet(target.AllowedTargetAgents, source.AllowedTargetAgents);
    }

    private static void ReplaceSet(HashSet<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

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
                break;

            case ClusteringMode.Localhost:
            default:
                client.UseLocalhostClustering();
                break;
        }

        logger.LogInformation(
            "FabrCore.Surface Orleans client clustering configured with mode {ClusteringMode}.",
            options.ClusteringMode);
    }

    private static void ConfigureConnectionRetry(IClientBuilder client, OrleansClusterOptions options, ILogger logger)
    {
        client.Configure<GatewayOptions>(gateway =>
        {
            gateway.GatewayListRefreshPeriod = options.GatewayListRefreshPeriod;
        });

        client.Configure<ClientMessagingOptions>(messaging =>
        {
            messaging.ResponseTimeout = TimeSpan.FromSeconds(30);
        });

        client.AddClusterConnectionLostHandler((_, _) =>
        {
            logger.LogWarning("FabrCore.Surface Orleans cluster connection lost.");
        });
    }
}

public sealed class SurfaceClientConnectionRetryFilter : IClientConnectionRetryFilter
{
    private readonly OrleansClusterOptions options;
    private readonly ILogger<SurfaceClientConnectionRetryFilter> logger;
    private int attemptCount;

    public SurfaceClientConnectionRetryFilter(
        IConfiguration configuration,
        ILogger<SurfaceClientConnectionRetryFilter> logger)
    {
        options = configuration.GetSection(OrleansClusterOptions.SectionName).Get<OrleansClusterOptions>()
                  ?? new OrleansClusterOptions();
        this.logger = logger;
    }

    public async Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken)
    {
        attemptCount++;
        var maxAttempts = options.ConnectionRetryCount + 1;
        if (attemptCount >= maxAttempts)
        {
            logger.LogError(exception, "FabrCore.Surface Orleans client connection failed after {MaxAttempts} attempts.", maxAttempts);
            return false;
        }

        logger.LogWarning(
            exception,
            "FabrCore.Surface Orleans client connection attempt {Attempt} of {MaxAttempts} failed; retrying in {Delay}.",
            attemptCount,
            maxAttempts,
            options.ConnectionRetryDelay);

        await Task.Delay(options.ConnectionRetryDelay, cancellationToken);
        return true;
    }
}
