using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Security;

public static class FabrCoreAdminAuthenticationDefaults
{
    public const string Scheme = "FabrCoreAdminApiKey";
    public const string Policy = "FabrCoreAdmin";
}

internal sealed class FabrCoreAdminAuthenticationHandler(
    IOptionsMonitor<FabrCoreAdminAuthenticationOptions> options,
    IOptionsMonitor<CloudServerOptions> cloudServerOptions,
    IOptionsMonitor<RemoteAdministrationOptions> remoteAdministrationOptions,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<FabrCoreAdminAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cloud = cloudServerOptions.CurrentValue;
        var cloudRemoteAdministrationKey = cloud.Enabled && remoteAdministrationOptions.CurrentValue.Enabled
            ? cloud.ApiKey
            : null;
        if (string.IsNullOrWhiteSpace(Options.ApiKey) &&
            string.IsNullOrWhiteSpace(cloudRemoteAdministrationKey))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"{FabrCoreAdminAuthenticationOptions.SectionName}:ApiKey is not configured, and " +
                $"{RemoteAdministrationOptions.SectionName}:Enabled is not true with an enabled Cloud Server API key."));
        }

        var authorization = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var suppliedKey = authorization[bearerPrefix.Length..].Trim();
        if (!FixedTimeEquals(Options.ApiKey, suppliedKey) &&
            !FixedTimeEquals(cloudRemoteAdministrationKey, suppliedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid administration API key."));
        }

        var principalId = string.IsNullOrWhiteSpace(Options.PrincipalId)
            ? "cluster-admin"
            : Options.PrincipalId.Trim();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, principalId), new Claim(ClaimTypes.Name, principalId)],
            FabrCoreAdminAuthenticationDefaults.Scheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            FabrCoreAdminAuthenticationDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool FixedTimeEquals(string? expected, string supplied)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
