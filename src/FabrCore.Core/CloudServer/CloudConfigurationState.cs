namespace FabrCore.Core.CloudServer;

/// <summary>A bounded, redacted observation. Receiving this document never changes desired configuration.</summary>
public sealed class CloudConfigurationState
{
    public string ApiVersion { get; set; } = "1";
    public string BootId { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string? DesiredRevision { get; set; }
    public string? RejectedRevision { get; set; }
    public string? Error { get; set; }
    public string ApplicationBuild { get; set; } = "";
    public bool Started { get; set; }
    public bool Truncated { get; set; }
    public List<CloudRuntimeSetting> Settings { get; set; } = [];
}

/// <summary>Desired, resolved and applied values have separate meanings. Null values may be redacted.</summary>
public sealed class CloudRuntimeSetting
{
    public string Key { get; set; } = "";
    public bool HasDesiredValue { get; set; }
    public string? DesiredValue { get; set; }
    public string? ResolvedValue { get; set; }
    public string? AppliedValue { get; set; }
    public bool AppliedKnown { get; set; }
    public string Source { get; set; } = "unknown";
    public string? SourceId { get; set; }
    public string? Reason { get; set; }
    public string ApplyMode { get; set; } = "RestartRequired";
    public bool Secret { get; set; }
    public bool Overridden { get; set; }
    public bool PendingRestart { get; set; }
    public bool CanAdopt { get; set; }
    public string? AppliedRevision { get; set; }
}
