using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceDiscoveryClient : ISurfaceDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfaceDiscoveryClient> logger;

    public SurfaceDiscoveryClient(
        HttpClient httpClient,
        IOptions<SurfaceOptions> options,
        ILogger<SurfaceDiscoveryClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<SurfaceDiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before loading Surface discovery.");
        }

        var baseUrl = options.FabrCoreHostUrl.TrimEnd('/');
        var url = $"{baseUrl}/fabrcoreapi/Discovery";

        logger.LogDebug("Loading Surface discovery metadata from {Url}.", url);

        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SurfaceDiscoveryResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Failed to deserialize Surface discovery response.");
    }
}
