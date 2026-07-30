using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfacePreferencesClient : ISurfacePreferencesClient
{
    private const string Container = "surface";
    private const string EntityKey = "command-center/preferences";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfacePreferencesClient> logger;

    public SurfacePreferencesClient(
        HttpClient httpClient,
        IOptions<SurfaceOptions> options,
        ILogger<SurfacePreferencesClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<SurfacePreferences> GetAsync(
        string principalId,
        SurfaceOptions defaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url = BuildUrl();
        logger.LogDebug("Loading Surface preferences from {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOwnerHeaders(request, principalId);

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return SurfacePreferences.FromDefaults(defaults);
        }

        response.EnsureSuccessStatusCode();
        var preferences = await response.Content.ReadFromJsonAsync<SurfacePreferences>(JsonOptions, cancellationToken)
                          ?? SurfacePreferences.FromDefaults(defaults);
        preferences.SurfaceAgentHandles = new HashSet<string>(preferences.SurfaceAgentHandles, StringComparer.OrdinalIgnoreCase);
        return preferences;
    }

    public async Task SaveAsync(
        string principalId,
        SurfacePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url = BuildUrl();
        logger.LogDebug("Saving Surface preferences to {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        AddOwnerHeaders(request, principalId);
        request.Content = JsonContent.Create(preferences, options: JsonOptions);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private string BuildUrl()
    {
        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before loading Surface preferences.");
        }

        return $"{options.FabrCoreHostUrl.TrimEnd('/')}/fabrcoreapi/Storage/{Container}/{EntityKey}";
    }

    private static void AddOwnerHeaders(HttpRequestMessage request, string principalId)
    {
        request.Headers.TryAddWithoutValidation("x-user", principalId);
        request.Headers.TryAddWithoutValidation("x-user-handle", principalId);
    }
}
