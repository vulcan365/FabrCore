using Microsoft.AspNetCore.Authentication;

namespace FabrCore.Host.Configuration;

public sealed class FabrCoreAdminAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SectionName = "FabrCore:AdminAuthentication";

    /// <summary>
    /// Cluster-scoped API key required by versioned administration endpoints.
    /// Store this value in user secrets, environment variables, or a secret manager.
    /// </summary>
    public string? ApiKey { get; set; }

    public string PrincipalId { get; set; } = "cluster-admin";
}
