using FabrCore.Core.Monitoring;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceMonitorTokenTotals
{
    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long ReasoningTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long LlmCalls { get; set; }

    public long Messages { get; set; }
}

public sealed class SurfaceMonitorTokenResponse
{
    public int AgentCount { get; set; }

    public SurfaceMonitorTokenTotals Totals { get; set; } = new();

    public List<AgentTokenSummary> Agents { get; set; } = [];
}

public sealed class SurfaceMonitorMessagesResponse
{
    public int Count { get; set; }

    public int Limit { get; set; }

    public List<MonitoredMessage> Messages { get; set; } = [];
}

public sealed class SurfaceMonitorEventsResponse
{
    public int Count { get; set; }

    public int Limit { get; set; }

    public List<MonitoredEvent> Events { get; set; } = [];
}

public sealed class SurfaceMonitorLlmCallsResponse
{
    public int Count { get; set; }

    public int Limit { get; set; }

    public bool PayloadsCaptured { get; set; }

    public List<MonitoredLlmCall> Calls { get; set; } = [];
}

public sealed class SurfaceMonitorErrorsResponse
{
    public DateTimeOffset? Since { get; set; }

    public int TotalErrors { get; set; }

    public Dictionary<string, int> ByAgent { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SurfaceMonitorErrorEntry> Recent { get; set; } = [];
}

public sealed class SurfaceMonitorErrorEntry
{
    public DateTimeOffset Timestamp { get; set; }

    public string? AgentHandle { get; set; }

    public string? Model { get; set; }

    public string? OriginContext { get; set; }

    public long DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ParentMessageId { get; set; }
}

public sealed class SurfaceMonitorConfigResponse
{
    public bool RecordingAvailable { get; set; }

    public string MonitorProvider { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool CapturePayloads { get; set; }

    public int MaxBufferedCalls { get; set; }

    public int MaxPayloadChars { get; set; }

    public int MaxToolArgsChars { get; set; }
}

public sealed class SurfaceMonitorConfigUpdate
{
    public bool? Enabled { get; set; }

    public bool? CapturePayloads { get; set; }

    public int? MaxBufferedCalls { get; set; }

    public int? MaxPayloadChars { get; set; }

    public int? MaxToolArgsChars { get; set; }
}
