namespace FabrCore.Surface.WebSocket;

/// <summary>How an app reaches its FabrCore host over <c>/ws</c> for Surface's chat.</summary>
public sealed class SurfaceWebSocketOptions
{
    /// <summary>The host's address. Without one, every chat says the app isn't connected (<see cref="SurfaceConnectionProblem.NotConfigured"/>).</summary>
    public Uri? HostUrl { get; set; }

    /// <summary>The key the host gave this app, sent as <see cref="ApiKeyHeader"/>. Left out for a host that needs none.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "x-api-key";

    /// <summary>The header naming the principal the app acts for, which the host's key check allows.</summary>
    public string PrincipalHeader { get; set; } = "x-user-handle";

    /// <summary>The named <see cref="HttpClient"/> each principal's connection uses, so an app can add handlers to it.</summary>
    public string HttpClientName { get; set; } = SurfaceWebSocketExtensions.HttpClientName;

    /// <summary>How long opening the connection may take before the chat says the host couldn't be reached.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a message waits with no word from its agent (no answer and no progress) before the chat stops waiting. Without it, a
    /// message to an agent that never started, or a host that restarted mid-answer, would leave the chat waiting for good.
    /// </summary>
    public TimeSpan QuietLimit { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Creates an agent this app created again when the host reports it isn't set up, as a restarted host may. Checking an agent's
    /// health before a message (as Surface does) then finds it ready instead of refusing the message.
    /// </summary>
    public bool RestoreCreatedAgents { get; set; } = true;

    /// <summary>What the chat shows when <see cref="QuietLimit"/> passes with no word from the agent.</summary>
    public string NoAnswerMessage { get; set; } =
        "No answer came, so this stopped waiting. The FabrCore host may have restarted, or the agent couldn't start. Ask again in a moment.";
}
