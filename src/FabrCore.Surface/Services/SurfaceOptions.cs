using FabrCore.Surface.Contracts;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Swarm;

namespace FabrCore.Surface.Services;

public enum SurfaceChatDeliveryMode
{
    FireAndForget,
    RequestResponse
}

public enum SurfaceChatMessageKind
{
    Request,
    OneWay
}

public enum SurfaceCommandCenterLayoutMode
{
    Standalone,
    Embedded
}

public sealed class SurfaceOptions
{
    private static readonly string[] DefaultPrincipalClaimTypes =
    [
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
        "oid",
        "sub"
    ];

    public string? DefinitionFilePath { get; set; }

    public string? DefaultSurfaceDefinitionName { get; set; }

    public string? DefaultPlanningModelName { get; set; }

    public List<string> PrincipalClaimTypes { get; set; } = [.. DefaultPrincipalClaimTypes];

    /// <summary>
    /// Request headers checked (in order) for a trusted principal id, e.g. "X-User-Id"
    /// forwarded by an authenticating reverse proxy. Empty by default: header identity is
    /// client-spoofable unless infrastructure strips inbound values, so hosts must opt in.
    /// </summary>
    public List<string> PrincipalHeaderNames { get; set; } = [];

    /// <summary>Headers checked for a display name when the principal is resolved from a header.</summary>
    public List<string> PrincipalDisplayNameHeaderNames { get; set; } = ["X-User-Name"];

    /// <summary>Applies <see cref="Identity.SurfacePrincipalId.Normalize"/> to every resolved principal id.</summary>
    public bool NormalizePrincipalIds { get; set; } = true;

    /// <summary>
    /// Optional host-supplied resolver consulted before the built-in resolution chain.
    /// Return null to fall through to the chain. Only settable from code; SurfaceOptions
    /// is copied via AddFabrCoreSurface, never bound from configuration.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, Task<Identity.SurfacePrincipalContext?>>? PrincipalResolver { get; set; }

    public string? DevelopmentFallbackPrincipalId { get; set; }

    public string? FabrCoreHostUrl { get; set; }

    public bool EnableAgentDirectory { get; set; } = true;

    public bool EnableAgentChat { get; set; } = true;

    public SurfaceChatDeliveryMode CommandCenterChatDeliveryMode { get; set; } = SurfaceChatDeliveryMode.FireAndForget;

    public SurfaceChatMessageKind CommandCenterChatMessageKind { get; set; } = SurfaceChatMessageKind.Request;

    public SurfaceCommandCenterLayoutMode CommandCenterLayoutMode { get; set; } = SurfaceCommandCenterLayoutMode.Embedded;

    public TimeSpan ChatFileUploadTtl { get; set; } = TimeSpan.FromMinutes(10);

    public long MaxChatAttachmentBytes { get; set; } = 100 * 1024 * 1024;

    public bool EnableAdaptiveCards { get; set; } = true;

    public bool EnableLiveStatus { get; set; } = true;

    public bool EnableSharedAgents { get; set; } = true;

    public bool ShowHiddenAgentsByDefault { get; set; }

    public bool ShowRunningAgentsByDefault { get; set; }

    public HashSet<string> DefaultSurfaceAgentHandles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> HiddenAgentTypes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "surface",
        SurfaceSwarmAgentTypes.Orchestrator,
        SurfaceSwarmAgentTypes.Planner,
        SurfaceSwarmAgentTypes.TaskRunner,
        SurfaceOrchestrationAgentTypes.SquadOrchestrator
    };

    public HashSet<string> HiddenAgentHandles { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "surface"
    };

    public bool EnableAgentCreate { get; set; }

    public bool EnableDiagnosticsPanel { get; set; }

    public string MaxAdaptiveCardVersion { get; set; } = "1.6";

    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    public int MaxDepth { get; set; } = 64;

    public bool AllowHttpUrls { get; set; }

    public HashSet<string> AllowedActionTypes { get; } = new(AdaptiveCardActionTypes.Defaults, StringComparer.OrdinalIgnoreCase);

    public bool AllowUnknownTargetAgents { get; set; }

    public HashSet<string> AllowedTargetAgents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool EnableDiagnostics { get; set; }
}
