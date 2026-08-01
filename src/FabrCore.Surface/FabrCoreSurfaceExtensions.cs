using FabrCore.Client.Orleans;
using FabrCore.Core.Blueprints;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Actions;
using FabrCore.Surface.Ai.Swarm;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Components;
using FabrCore.Surface.Identity;
using FabrCore.Surface.Services;
using FabrCore.Surface.Validation;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        return builder.AddFabrCoreSurface(options =>
        {
            options.DefinitionFilePath = definitionFilePath;
            options.DefaultSurfaceDefinitionName = resolvedDefinitionName;
        });
    }

    /// <summary>
    /// Registers Surface for a split client deployment by discovering provider-neutral Orleans
    /// gateways from the FabrCore Host before registering the normal <see cref="IClusterClient"/>.
    /// The caller owns <paramref name="discoveryHttpClient"/>, including authentication and its lifetime.
    /// </summary>
    public static Task<IHostApplicationBuilder> AddFabrCoreSurfaceFromConfigAsync(
        this IHostApplicationBuilder builder,
        HttpClient discoveryHttpClient,
        string definitionFilePath = "fabrcore-surface.json",
        string? definitionName = null,
        Action<FabrCoreOrleansClientOptions>? configureClient = null,
        Action<IClientBuilder>? configureOrleans = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionFilePath);

        var resolvedDefinitionName = string.IsNullOrWhiteSpace(definitionName) ? "default" : definitionName!;
        return builder.AddFabrCoreSurfaceAsync(
            discoveryHttpClient,
            options =>
            {
                options.DefinitionFilePath = definitionFilePath;
                options.DefaultSurfaceDefinitionName = resolvedDefinitionName;
            },
            configureClient,
            configureOrleans,
            cancellationToken);
    }

    /// <summary>
    /// Registers Surface for a split client deployment using FabrCore Host gateway discovery.
    /// SQL Server and Azure Storage clustering packages remain server-only dependencies.
    /// </summary>
    public static async Task<IHostApplicationBuilder> AddFabrCoreSurfaceAsync(
        this IHostApplicationBuilder builder,
        HttpClient discoveryHttpClient,
        Action<SurfaceOptions>? configure = null,
        Action<FabrCoreOrleansClientOptions>? configureClient = null,
        Action<IClientBuilder>? configureOrleans = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(discoveryHttpClient);

        var configuredOptions = CreateSurfaceOptions(configure);
        ApplySurfaceDefaults(builder, configuredOptions);

        var clusterClientDescriptor = builder.Services.FirstOrDefault(
            descriptor => descriptor.ServiceType == typeof(IClusterClient));
        if (clusterClientDescriptor is null)
        {
            await builder.AddFabrCoreOrleansClientAsync(
                discoveryHttpClient,
                options =>
                {
                    options.FabrCoreHostUrl = configuredOptions.FabrCoreHostUrl;
                    configureClient?.Invoke(options);
                },
                configureOrleans,
                cancellationToken).ConfigureAwait(false);
        }

        return AddSurfaceServices(builder, configuredOptions);
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

            var configuredOptions = CreateSurfaceOptions(configure);
            if (orleansOptions.ClusteringMode != ClusteringMode.Localhost)
            {
                throw new InvalidOperationException(
                    $"FabrCore:Orleans:ClusteringMode is '{orleansOptions.ClusteringMode}'. Split Surface clients no longer " +
                    "load SQL Server or Azure Storage clustering providers. Call AddFabrCoreSurfaceAsync with an " +
                    "authenticated discovery HttpClient, or configure IClusterClient before AddFabrCoreSurface.");
            }

            builder.UseOrleansClient(client =>
            {
                client.UseLocalhostClustering();
                ConfigureConnectionRetry(client, orleansOptions, logger);
                client.UseConnectionRetryFilter<SurfaceClientConnectionRetryFilter>();
            });

            return AddSurfaceServices(builder, configuredOptions);
        }

        logger.LogInformation("Orleans client already configured; FabrCore.Surface will reuse it.");
        return AddSurfaceServices(builder, CreateSurfaceOptions(configure));
    }

    private static SurfaceOptions CreateSurfaceOptions(Action<SurfaceOptions>? configure)
    {
        var configuredOptions = new SurfaceOptions();
        configure?.Invoke(configuredOptions);
        return configuredOptions;
    }

    private static IHostApplicationBuilder AddSurfaceServices(
        IHostApplicationBuilder builder,
        SurfaceOptions configuredOptions)
    {
        ApplySurfaceDefaults(builder, configuredOptions);

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
        builder.Services.AddFabrCoreSurfaceNavigation();

        builder.Services.TryAddSingleton<ISurfacePrincipalContextFactory, SurfacePrincipalContextFactory>();
        builder.Services.TryAddSingleton<ISurfaceDirectMessageSender, SurfaceDirectMessageSender>();
        builder.Services.TryAddSingleton<ISurfaceBasicSquadService, SurfaceBasicSquadService>();
        builder.Services.AddOptions<SurfaceSwarmOptions>();
        builder.Services.TryAddSingleton<ISurfaceSquadService, SurfaceSquadService>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBlueprintExpander, SurfaceSwarmBlueprintExpander>());
        builder.Services.TryAddSingleton<SurfaceTranscriptStore>();
        builder.Services.AddHttpClient<ISurfaceDiscoveryClient, SurfaceDiscoveryClient>();
        builder.Services.AddHttpClient<ISurfaceMonitorClient, SurfaceMonitorClient>();
        builder.Services.AddHttpClient<ISurfacePreferencesClient, SurfacePreferencesClient>();
        builder.Services.AddHttpClient<ISurfaceSquadConfigClient, SurfaceSquadConfigClient>();
        builder.Services.AddHttpClient<ISurfaceBlueprintClient, SurfaceBlueprintClient>();
        builder.Services.TryAddScoped<IFabrCoreHostApiClient>(CreateFabrCoreHostApiClient);
        builder.Services.TryAddScoped<ISurfaceFileUploadClient, SurfaceFileUploadClient>();
        builder.Services.TryAddScoped<SurfaceBlueprintProvisioner>();
        builder.Services.TryAddSingleton<ISurfaceActionRegistry, EmptySurfaceActionRegistry>();
        builder.Services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.TryAddScoped<SurfacePrincipalAccessor>();
        builder.Services.TryAddScoped<ISurfacePrincipalContextProvider, DefaultSurfacePrincipalContextProvider>();
        builder.Services.TryAddScoped<SurfaceWorkspaceService>();
        builder.Services.TryAddScoped<AdaptiveCardSurfaceValidator>();
        builder.Services.TryAddScoped<ISurfaceActionDispatcher, SurfaceActionDispatcher>();

        return builder;
    }

    private static void ApplySurfaceDefaults(IHostApplicationBuilder builder, SurfaceOptions configuredOptions)
    {
        configuredOptions.FabrCoreHostUrl ??=
            builder.Configuration[FabrCoreOrleansClientOptions.FabrCoreHostUrlConfigurationKey] ??
            "http://localhost:5000";
    }

    public static IServiceCollection AddFabrCoreSurfaceComponents(this IServiceCollection services)
    {
        services.AddOptions<SurfaceOptions>();
        services.AddFabrCoreSurfaceNavigation();
        services.TryAddSingleton<ISurfaceBasicSquadService, SurfaceBasicSquadService>();
        services.AddOptions<SurfaceSwarmOptions>();
        services.TryAddSingleton<ISurfaceSquadService, SurfaceSquadService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBlueprintExpander, SurfaceSwarmBlueprintExpander>());
        services.TryAddSingleton<SurfaceTranscriptStore>();
        services.AddHttpClient<ISurfaceDiscoveryClient, SurfaceDiscoveryClient>();
        services.AddHttpClient<ISurfaceMonitorClient, SurfaceMonitorClient>();
        services.AddHttpClient<ISurfacePreferencesClient, SurfacePreferencesClient>();
        services.AddHttpClient<ISurfaceSquadConfigClient, SurfaceSquadConfigClient>();
        services.AddHttpClient<ISurfaceBlueprintClient, SurfaceBlueprintClient>();
        services.TryAddScoped<IFabrCoreHostApiClient>(CreateFabrCoreHostApiClient);
        services.TryAddScoped<ISurfaceFileUploadClient, SurfaceFileUploadClient>();
        services.TryAddScoped<SurfaceBlueprintProvisioner>();
        services.TryAddSingleton<ISurfaceActionRegistry, EmptySurfaceActionRegistry>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.TryAddScoped<SurfacePrincipalAccessor>();
        services.TryAddScoped<ISurfacePrincipalContextProvider, DefaultSurfacePrincipalContextProvider>();
        services.TryAddScoped<SurfaceWorkspaceService>();
        services.TryAddScoped<AdaptiveCardSurfaceValidator>();
        services.TryAddScoped<ISurfaceActionDispatcher, SurfaceActionDispatcher>();
        return services;
    }

    internal static IServiceCollection AddFabrCoreSurfaceNavigation(this IServiceCollection services)
    {
        services.AddOptions<SurfaceNavigationOptions>()
            .Configure(options => options.SurfaceLoaded = true);
        return services;
    }

    public static RazorComponentsEndpointConventionBuilder AddFabrCoreSurfaceRoutes(
        this RazorComponentsEndpointConventionBuilder builder)
    {
        return builder.AddFabrCoreSurfaceRouteAssemblies(typeof(SurfaceCommandCenter).Assembly);
    }

    // Razor component discovery throws "Assembly already defined" at startup when the same
    // assembly is passed to AddAdditionalAssemblies twice on one MapRazorComponents builder.
    // FabrCore Surface route extensions layer on each other (AddFabrCoreSurfaceAdminRoutes
    // includes AddFabrCoreSurfaceRoutes), so registration must stay idempotent per builder.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        RazorComponentsEndpointConventionBuilder,
        HashSet<System.Reflection.Assembly>> RegisteredRouteAssemblies = new();

    public static RazorComponentsEndpointConventionBuilder AddFabrCoreSurfaceRouteAssemblies(
        this RazorComponentsEndpointConventionBuilder builder,
        params System.Reflection.Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assemblies);

        var registered = RegisteredRouteAssemblies.GetOrCreateValue(builder);
        lock (registered)
        {
            foreach (var assembly in assemblies)
            {
                if (registered.Add(assembly))
                {
                    builder.AddAdditionalAssemblies(assembly);
                }
            }
        }

        return builder;
    }

    private static void CopySurfaceOptions(SurfaceOptions source, SurfaceOptions target)
    {
        target.DefinitionFilePath = source.DefinitionFilePath;
        target.DefaultSurfaceDefinitionName = source.DefaultSurfaceDefinitionName;
        target.DefaultPlanningModelName = source.DefaultPlanningModelName;
        target.PrincipalClaimTypes = [.. source.PrincipalClaimTypes];
        target.PrincipalHeaderNames = [.. source.PrincipalHeaderNames];
        target.PrincipalDisplayNameHeaderNames = [.. source.PrincipalDisplayNameHeaderNames];
        target.NormalizePrincipalIds = source.NormalizePrincipalIds;
        target.PrincipalResolver = source.PrincipalResolver;
        target.DevelopmentFallbackPrincipalId = source.DevelopmentFallbackPrincipalId;
        target.FabrCoreHostUrl = source.FabrCoreHostUrl;
        target.EnableAgentDirectory = source.EnableAgentDirectory;
        target.EnableAgentChat = source.EnableAgentChat;
        target.CommandCenterChatDeliveryMode = source.CommandCenterChatDeliveryMode;
        target.CommandCenterChatMessageKind = source.CommandCenterChatMessageKind;
        target.CommandCenterLayoutMode = source.CommandCenterLayoutMode;
        target.ChatFileUploadTtl = source.ChatFileUploadTtl;
        target.MaxChatAttachmentBytes = source.MaxChatAttachmentBytes;
        target.EnableAdaptiveCards = source.EnableAdaptiveCards;
        target.EnableLiveStatus = source.EnableLiveStatus;
        target.EnableSharedAgents = source.EnableSharedAgents;
        target.ShowHiddenAgentsByDefault = source.ShowHiddenAgentsByDefault;
        target.ShowRunningAgentsByDefault = source.ShowRunningAgentsByDefault;
        target.EnableAgentCreate = source.EnableAgentCreate;
        target.EnableDiagnosticsPanel = source.EnableDiagnosticsPanel;
        target.MaxAdaptiveCardVersion = source.MaxAdaptiveCardVersion;
        target.MaxPayloadBytes = source.MaxPayloadBytes;
        target.MaxDepth = source.MaxDepth;
        target.AllowHttpUrls = source.AllowHttpUrls;
        target.AllowUnknownTargetAgents = source.AllowUnknownTargetAgents;
        target.EnableDiagnostics = source.EnableDiagnostics;

        ReplaceSet(target.AllowedActionTypes, source.AllowedActionTypes);
        ReplaceSet(target.AllowedTargetAgents, source.AllowedTargetAgents);
        ReplaceSet(target.DefaultSurfaceAgentHandles, source.DefaultSurfaceAgentHandles);
        ReplaceSet(target.HiddenAgentTypes, source.HiddenAgentTypes);
        ReplaceSet(target.HiddenAgentHandles, source.HiddenAgentHandles);
    }

    private static void ReplaceSet(HashSet<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static IFabrCoreHostApiClient CreateFabrCoreHostApiClient(IServiceProvider serviceProvider)
    {
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var options = serviceProvider.GetRequiredService<IOptions<SurfaceOptions>>().Value;
        var logger = serviceProvider.GetRequiredService<ILogger<FabrCoreHostApiClient>>();

        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            return new FabrCoreHostApiClient(
                httpClientFactory.CreateClient(nameof(FabrCoreHostApiClient)),
                configuration,
                logger);
        }

        var hostApiConfiguration = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [FabrCore.Core.FabrCoreConfigurationKeys.HostUrl] = options.FabrCoreHostUrl
            })
            .Build();

        return new FabrCoreHostApiClient(
            httpClientFactory.CreateClient(nameof(FabrCoreHostApiClient)),
            hostApiConfiguration,
            logger);
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
