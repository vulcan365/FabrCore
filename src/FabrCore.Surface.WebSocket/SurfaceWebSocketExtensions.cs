using FabrCore.Sdk;
using FabrCore.Surface.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FabrCore.Surface.WebSocket;

public static class SurfaceWebSocketExtensions
{
    /// <summary>The named <see cref="HttpClient"/> behind each principal's connection (<see cref="SurfaceWebSocketOptions.HttpClientName"/>).</summary>
    public const string HttpClientName = "FabrCore.Surface.WebSocket";

    /// <summary>
    /// Surface's chat over the host's <c>/ws</c>, for Blazor Server apps without an Orleans cluster connection: each principal gets one
    /// <see cref="SurfaceWebSocketPrincipalContext"/>, shared by everything it has open. Pages then use <see cref="SurfaceChat"/> or
    /// <see cref="SurfaceChatLink"/>. Who the signed-in person is comes from <see cref="SurfaceOptions.PrincipalResolver"/> (or Surface's
    /// claims), and the host's key must be allowed to act for that principal.
    /// </summary>
    public static IServiceCollection AddFabrCoreSurfaceWebSocket(
        this IServiceCollection services,
        Action<SurfaceWebSocketOptions> configure,
        Action<SurfaceOptions>? configureSurface = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SurfaceWebSocketOptions>().Configure(configure);
        services.AddHttpClient(HttpClientName);
        services.AddFabrCoreSurfaceChat();
        services.AddOptions<SurfaceOptions>().Configure<Microsoft.Extensions.Options.IOptions<SurfaceWebSocketOptions>>((surface, socket) =>
        {
            // Answers arrive as deliveries over the socket. A request that waits for the answer is cut off by proxies (Container Apps
            // gives up after 240 seconds), so chat never waits on the send.
            surface.CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget;
            surface.FabrCoreHostUrl ??= socket.Value.HostUrl?.ToString();
            configureSurface?.Invoke(surface);
        });

        // The host API client (file uploads) carries the app's key too.
        services.AddHttpClient(nameof(FabrCoreHostApiClient)).ConfigureHttpClient((provider, client) =>
        {
            var socket = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SurfaceWebSocketOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(socket.ApiKey))
                client.DefaultRequestHeaders.TryAddWithoutValidation(socket.ApiKeyHeader, socket.ApiKey);
        });

        services.RemoveAll<ISurfacePrincipalContextFactory>();
        services.TryAddSingleton<SurfaceWebSocketPrincipalContextFactory>();
        services.AddSingleton<ISurfacePrincipalContextFactory>(provider => provider.GetRequiredService<SurfaceWebSocketPrincipalContextFactory>());
        return services;
    }
}
