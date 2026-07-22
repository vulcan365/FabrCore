using FabrCore.Core.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;

namespace FabrCore.Client.Orleans;

/// <summary>Registration extensions for provider-neutral FabrCore Orleans clients.</summary>
public static class FabrCoreOrleansClientExtensions
{
    /// <summary>
    /// Configures an Orleans client from a previously fetched discovery document and registers
    /// the HTTP-backed gateway provider used for subsequent refreshes.
    /// </summary>
    public static IClientBuilder UseFabrCoreHostClustering(
        this IClientBuilder builder,
        FabrCoreGatewayDiscoveryClient discoveryClient,
        FabrCoreGatewayDiscoveryDocument initialDocument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(initialDocument);

        discoveryClient.Validate(initialDocument);
        var refreshPeriod = TimeSpan.FromSeconds(initialDocument.RefreshPeriodSeconds);

        builder.Configure<ClusterOptions>(cluster =>
        {
            cluster.ClusterId = initialDocument.ClusterId;
            cluster.ServiceId = initialDocument.ServiceId;
        });
        builder.Configure<GatewayOptions>(gateways =>
            gateways.GatewayListRefreshPeriod = refreshPeriod);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(discoveryClient);
            services.AddSingleton(initialDocument);
            services.AddSingleton<FabrCoreHostGatewayListProvider>();
            services.AddSingleton<IGatewayListProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<FabrCoreHostGatewayListProvider>());
        });

        return builder;
    }

    /// <summary>
    /// Fetches the discovery document before Orleans registration, configures the
    /// discovered cluster identity and gateways, applies caller Orleans configuration (including
    /// TLS), and registers Orleans' normal <see cref="IClusterClient"/> singleton.
    /// </summary>
    public static async Task<IHostApplicationBuilder> AddFabrCoreOrleansClientAsync(
        this IHostApplicationBuilder builder,
        HttpClient discoveryHttpClient,
        Action<FabrCoreOrleansClientOptions>? configureOptions = null,
        Action<IClientBuilder>? configureOrleans = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(discoveryHttpClient);

        var options = new FabrCoreOrleansClientOptions
        {
            FabrCoreHostUrl = builder.Configuration[
                FabrCoreOrleansClientOptions.FabrCoreHostUrlConfigurationKey]
        };
        configureOptions?.Invoke(options);

        var discoveryClient = new FabrCoreGatewayDiscoveryClient(discoveryHttpClient, options);
        var document = await discoveryClient
            .GetGatewayDiscoveryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (document.RequireOrleansTls && configureOrleans is null)
        {
            throw new FabrCoreGatewayDiscoveryException(
                "The FabrCore Host requires Orleans TLS. Supply the Orleans configuration callback and call UseTls with the application's certificate configuration.");
        }

        builder.UseOrleansClient(orleans =>
        {
            orleans.UseFabrCoreHostClustering(discoveryClient, document);
            configureOrleans?.Invoke(orleans);
        });

        return builder;
    }
}
