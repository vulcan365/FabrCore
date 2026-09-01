using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>Claim types issued by the A2A API key handler.</summary>
public static class A2AClaimTypes
{
    /// <summary>Name of the API key entry that authenticated the request.</summary>
    public const string ApiKeyName = "fabrcore:a2a:apikey";

    /// <summary>FabrCore principal handle the matched API key acts as, when configured.</summary>
    public const string PrincipalHandle = "fabrcore:a2a:principal";

    /// <summary>Comma-separated route names the matched API key is limited to. Absent means all.</summary>
    public const string AllowedAgents = "fabrcore:a2a:agents";
}

/// <summary>
/// Authenticates A2A callers by a shared secret presented in a header (or, when configured, a
/// query parameter). This is the scheme Copilot Studio's "API key" authentication option produces.
/// </summary>
internal sealed class A2AApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<A2AOptions> _a2aOptions;

    public A2AApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        IOptionsMonitor<A2AOptions> a2aOptions,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
        => _a2aOptions = a2aOptions;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKeyOptions = _a2aOptions.CurrentValue.Authentication.ApiKey;

        if (apiKeyOptions.Keys.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "A2A:Authentication:ApiKey:Keys is empty, so no caller can be authenticated."));
        }

        var supplied = ReadKey(apiKeyOptions);
        if (string.IsNullOrEmpty(supplied))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Compare against every configured key rather than stopping at the first match, so the
        // work done is the same whichever key was presented (and whether or not one matched).
        A2AApiKeyEntry? matched = null;
        foreach (var entry in apiKeyOptions.Keys)
        {
            if (FixedTimeEquals(entry.Value, supplied))
            {
                matched ??= entry;
            }
        }

        if (matched is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid A2A API key."));
        }

        var name = string.IsNullOrWhiteSpace(matched.Name) ? "a2a-client" : matched.Name!.Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, name),
            new(ClaimTypes.Name, name),
            new(A2AClaimTypes.ApiKeyName, name),
        };

        if (!string.IsNullOrWhiteSpace(matched.PrincipalHandle))
        {
            claims.Add(new Claim(A2AClaimTypes.PrincipalHandle, matched.PrincipalHandle!.Trim()));
        }

        if (matched.Agents.Count > 0)
        {
            claims.Add(new Claim(A2AClaimTypes.AllowedAgents, string.Join(',', matched.Agents)));
        }

        var identity = new ClaimsIdentity(claims, A2ADefaults.ApiKeyScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), A2ADefaults.ApiKeyScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ReadKey(A2AApiKeyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HeaderName)
            && Request.Headers.TryGetValue(options.HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString().Trim();
        }

        // Bearer is accepted on the standard Authorization header too: several A2A clients send
        // an API key that way, and the card advertises whichever location was configured.
        var authorization = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorization[bearerPrefix.Length..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.QueryParameterName)
            && Request.Query.TryGetValue(options.QueryParameterName!, out var query)
            && !string.IsNullOrWhiteSpace(query))
        {
            return query.ToString().Trim();
        }

        return null;
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
