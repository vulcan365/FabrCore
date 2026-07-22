using Microsoft.Extensions.Options;

namespace FabrCore.Host.Configuration;

/// <summary>
/// Configures provider-neutral Orleans gateway discovery at
/// <c>FabrCore:Host:GatewayDiscovery</c>.
/// </summary>
public sealed class GatewayDiscoveryOptions
{
    public const string SectionName = "FabrCore:Host:GatewayDiscovery";

    /// <summary>Gets or sets whether the gateway discovery endpoint is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the ASP.NET Core authorization policy required to call the endpoint.
    /// This value is required when discovery is enabled.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>Gets or sets the gateway refresh period returned to clients.</summary>
    public TimeSpan RefreshPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets explicit public gateway URIs. When non-empty, these values take
    /// precedence over addresses derived from Orleans membership.
    /// </summary>
    public List<string> AdvertisedGateways { get; set; } = [];

    /// <summary>Gets or sets whether clients must configure Orleans transport TLS.</summary>
    public bool RequireOrleansTls { get; set; }
}

internal sealed class GatewayDiscoveryOptionsValidator : IValidateOptions<GatewayDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayDiscoveryOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            failures.Add($"{GatewayDiscoveryOptions.SectionName}:AuthorizationPolicy is required when gateway discovery is enabled.");
        }

        if (options.RefreshPeriod <= TimeSpan.Zero)
        {
            failures.Add($"{GatewayDiscoveryOptions.SectionName}:RefreshPeriod must be greater than zero.");
        }
        else if (options.RefreshPeriod.TotalSeconds > int.MaxValue)
        {
            failures.Add($"{GatewayDiscoveryOptions.SectionName}:RefreshPeriod must not exceed {int.MaxValue} seconds.");
        }

        foreach (var gateway in options.AdvertisedGateways)
        {
            if (!GatewayUriUtilities.TryParse(gateway, out _))
            {
                failures.Add($"{GatewayDiscoveryOptions.SectionName}:AdvertisedGateways contains invalid Orleans gateway URI '{gateway}'. Expected gwy.tcp://host:port/0.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
