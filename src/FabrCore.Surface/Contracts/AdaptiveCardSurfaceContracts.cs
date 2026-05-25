using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Surface.Services;

namespace FabrCore.Surface.Contracts;

public sealed class AdaptiveCardSurfaceEnvelope
{
    public string Version { get; set; } = "2.0";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public JsonElement Card { get; set; }

    public JsonElement? Data { get; set; }

    public AdaptiveCardSurfaceMetadata? Metadata { get; set; }
}

public sealed class AdaptiveCardSurfaceMetadata
{
    public string? TargetHandle { get; set; }

    public string? RouteTo { get; set; }

    public string? TargetAgent { get; set; }

    public string? MessageTemplate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdaptiveCardSurfaceAction
{
    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Verb { get; set; }

    public string? Url { get; set; }

    public JsonElement? Data { get; set; }

    public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdaptiveCardActionEvent
{
    public string Version { get; set; } = "2.0";

    public string? EnvelopeId { get; set; }

    public string Kind { get; set; } = SurfaceMessageTypes.UiAction;

    public string ActionType { get; set; } = string.Empty;

    public string ActionId { get; set; } = string.Empty;

    public string? Verb { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }

    public string RouteTo { get; set; } = SurfaceActionRoute.App;

    public string? Message { get; set; }

    public string? TargetAgent { get; set; }

    public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SurfaceActionResult? Result { get; set; }
}

public static class AdaptiveCardActionTypes
{
    public const string Execute = "Action.Execute";
    public const string Submit = "Action.Submit";
    public const string OpenUrl = "Action.OpenUrl";
    public const string ShowCard = "Action.ShowCard";
    public const string ToggleVisibility = "Action.ToggleVisibility";

    public static readonly string[] Defaults =
    [
        Execute,
        Submit,
        OpenUrl,
        ShowCard,
        ToggleVisibility
    ];
}

public static class SurfaceActionRoute
{
    public const string App = "app";
    public const string Agent = "agent";
    public const string Both = "both";
}

public static class SurfaceMessageArgs
{
    public const string TargetHandle = "targetHandle";
    public const string SurfaceTargetHandle = "surface:TargetHandle";
    public const string SurfaceConfig = "surface:Config";
}

public static class SurfaceActionDataKeys
{
    public const string ActionId = "actionId";
    public const string RouteTo = "routeTo";
    public const string TargetAgent = "targetAgent";
    public const string MessageTemplate = "messageTemplate";
}

public static class SurfaceDiagnosticArgs
{
    public const string TargetHandle = "_surface_target_handle";
    public const string PlannedActionCount = "_surface_planned_action_count";
    public const string ValidatedActionCount = "_surface_validated_action_count";
    public const string RejectedActionCount = "_surface_rejected_action_count";
    public const string RenderedActionCount = "_surface_rendered_action_count";
}
