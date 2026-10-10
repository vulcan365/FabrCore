using FabrCore.Host.Configuration.Cloud;
using FabrCore.Services.Contracts.Capabilities;
using Microsoft.Extensions.Options;
using Marker = FabrCore.Services.Microsoft365Copilot.Microsoft365CopilotExtensions.Microsoft365CopilotMarker;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Advertises the channel to management consoles. Registered even while the channel is off, so a
/// console can tell "installed but disabled" from "not installed".
/// </summary>
internal sealed class CopilotCapabilityContributor(IOptions<Microsoft365CopilotOptions> options, Marker marker)
    : IFabrCoreCapabilityContributor
{
    internal const string ServiceName = "microsoft365-copilot";
    internal const string HeartbeatFlag = "m365copilot";

    public IEnumerable<ClusterServiceCapability> GetServices()
    {
        var value = options.Value;
        // The administration routes answer whether or not the channel is on.
        var features = new List<string> { "status", "app-package", "diagnostics" };
        if (marker.Enabled)
        {
            features.Add("activity-protocol");
            if (value.Streaming.Enabled) features.Add("streaming");
            features.Add("adaptive-cards");
            features.Add("principal-" + value.Principal.Strategy.ToString().ToLowerInvariant());
            if (value.Proactive.Enabled) features.Add("proactive");
            if (value.UserAuthorizationConfigured) features.Add("sso");
            if (!string.IsNullOrWhiteSpace(value.Agent.Binding)) features.Add("agent-binding");
            if (!value.TokenValidation.Enabled) features.Add("token-validation-off");
        }

        yield return new ClusterServiceCapability
        {
            Name = ServiceName,
            Version = typeof(CopilotCapabilityContributor).Assembly.GetName().Version?.ToString(),
            ApiVersion = FabrCore.Core.CloudServer.IntegrationAdministration.ApiVersion,
            Features = features,
            Available = marker.Enabled,
            UnavailableReason = marker.Enabled ? null : marker.DisabledReason
        };
    }

    public IReadOnlyDictionary<string, string> GetHeartbeatCapabilities() => new Dictionary<string, string>
    {
        [HeartbeatFlag] = "1",
        [HeartbeatFlag + ".enabled"] = marker.Enabled ? "true" : "false"
    };
}

/// <summary>
/// Reports the channel settings actually in effect to the configuration-state report, so a
/// console can show what is applied on each host and what is waiting on a restart.
/// </summary>
/// <remarks>
/// Key names under <c>Runtime:</c> avoid the words the settings catalog treats as secret
/// ("token", "secret"), because a redacted fact tells an operator nothing.
/// </remarks>
internal sealed class CopilotRuntimeSettingsContributor(IOptions<Microsoft365CopilotOptions> options, Marker marker)
    : IFabrCoreRuntimeSettingsContributor
{
    internal const string SourceId = "Microsoft 365 Copilot add-on";
    internal const string SingleSignOnFact = "Runtime:Microsoft365Copilot:SingleSignOn";
    internal const string ForwardsUserCredentialFact = "Runtime:Microsoft365Copilot:ForwardsUserCredential";
    private const string OffReason = "The channel is off, so nothing consumes this setting.";

    public IEnumerable<RuntimeSettingObservation> Observe()
    {
        var value = options.Value;
        var defaults = new Microsoft365CopilotOptions();

        // An add-on nobody configured starts off, whatever the options class default says.
        yield return new(Key("Enabled"), marker.Enabled.ToString(), SourceId)
        {
            DefaultValue = marker.Configured ? defaults.Enabled.ToString() : bool.FalseString
        };
        yield return Setting("TenantId", value.TenantId);
        yield return Setting("ClientId", value.ClientId);
        yield return marker.NativeServiceConnection
            ? new(Key("AuthType"), value.AuthType, SourceId, AppliedKnown: false,
                Reason: "The host configures the Agents SDK Connections section itself, so this setting is not what authenticates.")
            { DefaultValue = defaults.AuthType }
            : Setting("AuthType", value.AuthType, defaults.AuthType);
        yield return Setting("MessagesEndpoint", value.MessagesEndpoint, defaults.MessagesEndpoint);
        yield return Setting("TokenValidation:Enabled", value.TokenValidation.Enabled.ToString(), defaults.TokenValidation.Enabled.ToString());
        yield return Setting("Principal:Strategy", value.Principal.Strategy.ToString(), defaults.Principal.Strategy.ToString());
        yield return Setting("Agent:Binding", value.Agent.Binding);
        yield return Setting("Proactive:Enabled", value.Proactive.Enabled.ToString(), defaults.Proactive.Enabled.ToString());
        yield return Setting("Streaming:Enabled", value.Streaming.Enabled.ToString(), defaults.Streaming.Enabled.ToString());
        yield return Setting("Manifest:PublicHostName", value.Manifest.PublicHostName);

        yield return Fact(SingleSignOnFact, value.UserAuthorizationConfigured);
        yield return Fact(ForwardsUserCredentialFact, CopilotIntegrationStatusBuilder.ForwardsUserCredential(value));
    }

    private static string Key(string name) => Microsoft365CopilotDefaults.SectionName + ":" + name;

    private RuntimeSettingObservation Setting(string name, string? value, string? defaultValue = null) =>
        new(Key(name), value, SourceId, AppliedKnown: marker.Enabled, Reason: marker.Enabled ? null : OffReason)
        {
            DefaultValue = defaultValue
        };

    private RuntimeSettingObservation Fact(string key, bool value) =>
        new(key, value.ToString(), SourceId, AppliedKnown: marker.Enabled, Reason: marker.Enabled ? null : OffReason);
}
