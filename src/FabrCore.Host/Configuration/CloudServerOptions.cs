using Microsoft.Extensions.Options;

namespace FabrCore.Host.Configuration;

/// <summary>
/// Configures the Cloud Server feature at <c>FabrCore:CloudServer</c>. When enabled, the host
/// pulls its model/API-key configuration from a remote cloud server implementing the FabrCore
/// cloud server protocol (see docs/cloud-server-protocol.md) instead of the local
/// fabrcore.json, and reports periodic heartbeats. Disabled by default — existing hosts are
/// unaffected. The API key is supplied by the operator; securing it (user secrets,
/// environment variables, a vault-backed configuration provider) is the operator's
/// responsibility.
/// </summary>
public sealed class CloudServerOptions
{
    public const string SectionName = "FabrCore:CloudServer";

    /// <summary>The hosted FabrCore Forge endpoint used when no Url override is configured.</summary>
    public const string DefaultUrl = "https://forge.vulcan365.ai";

    /// <summary>Gets or sets whether cloud-delivered configuration is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the cloud server base URL.</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>Gets or sets the per-cluster API key presented as a bearer token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the cluster identifier sent to the cloud server. Null falls back to the
    /// Orleans <c>ClusterOptions.ClusterId</c>.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary>
    /// Gets or sets the environment name sent to the cloud server for appsettings-style
    /// configuration layering. Null falls back to <c>IHostEnvironment.EnvironmentName</c>.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>Gets or sets how often the host checks the cloud server for updated configuration.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the timeout applied to individual cloud server requests.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether the last successfully fetched configuration is cached to disk so
    /// the host can start while the cloud server is unreachable. The cache file has the same
    /// secrets-on-disk profile as fabrcore.json.
    /// </summary>
    public bool CacheLastKnownGood { get; set; } = true;

    /// <summary>
    /// Gets or sets the cache file path. Null defaults to fabrcore.cloud-cache.json in the
    /// content root.
    /// </summary>
    public string? CacheFilePath { get; set; }

    /// <summary>
    /// Gets or sets what happens when the cloud server is unreachable at startup and no disk
    /// cache is available.
    /// </summary>
    public CloudServerStartupFailureBehavior StartupFailureBehavior { get; set; } = CloudServerStartupFailureBehavior.Fail;

    /// <summary>Gets or sets heartbeat reporting options.</summary>
    public CloudServerHeartbeatOptions Heartbeat { get; set; } = new();
}

/// <summary>Startup behavior when cloud configuration cannot be obtained from network or cache.</summary>
public enum CloudServerStartupFailureBehavior
{
    /// <summary>Fail host startup with a clear error (default) — avoids opaque model 404s later.</summary>
    Fail,

    /// <summary>Start with no configuration; model/key lookups return 404 until a sync succeeds.</summary>
    StartDegraded
}

/// <summary>Heartbeat reporting options nested under <c>FabrCore:CloudServer:Heartbeat</c>.</summary>
public sealed class CloudServerHeartbeatOptions
{
    /// <summary>Gets or sets whether heartbeats are sent. Heartbeat failures are never fatal.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the heartbeat interval.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class CloudServerOptionsValidator : IValidateOptions<CloudServerOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudServerOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{CloudServerOptions.SectionName}:Url must be an absolute http(s) URL. Got '{options.Url}'.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{CloudServerOptions.SectionName}:ApiKey is required when the Cloud Server feature is enabled.");
        }

        if (options.RefreshInterval <= TimeSpan.Zero)
        {
            failures.Add($"{CloudServerOptions.SectionName}:RefreshInterval must be greater than zero.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{CloudServerOptions.SectionName}:RequestTimeout must be greater than zero.");
        }

        if (options.Heartbeat.Enabled && options.Heartbeat.Interval <= TimeSpan.Zero)
        {
            failures.Add($"{CloudServerOptions.SectionName}:Heartbeat:Interval must be greater than zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
