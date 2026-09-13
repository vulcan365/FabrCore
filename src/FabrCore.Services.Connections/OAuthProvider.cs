using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FabrCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Connections;

public interface IConnectionCredentialProvider
{
    Task<Dictionary<string, string>> GetCredentialAsync(ConnectionProfile profile, string tokenEndpoint, CancellationToken cancellationToken);
}

/// <summary>References resolve only in server-side configuration (including vault providers).</summary>
internal sealed class ConfigurationCredentialProvider(IConfiguration configuration) : IConnectionCredentialProvider
{
    public Task<Dictionary<string, string>> GetCredentialAsync(ConnectionProfile profile, string tokenEndpoint, CancellationToken cancellationToken)
    {
        var reference = profile.CredentialReference;
        if (string.IsNullOrWhiteSpace(reference)) return Task.FromResult(new Dictionary<string, string>());
        var section = configuration.GetSection("FabrCore:ConnectionCredentials:" + reference);
        if (section["Secret"] is { Length: > 0 } secret)
            return Task.FromResult(new Dictionary<string, string> { ["client_secret"] = secret });
        if (section["CertificatePath"] is { Length: > 0 } path)
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, section["CertificatePassword"]);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(profile.ClientId, tokenEndpoint,
                [new Claim("sub", profile.ClientId), new Claim("jti", Guid.NewGuid().ToString())], now, now.AddMinutes(5),
                new X509SigningCredentials(certificate, SecurityAlgorithms.RsaSha256));
            return Task.FromResult(new Dictionary<string, string> {
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = new JwtSecurityTokenHandler().WriteToken(token) });
        }
        throw new ConnectionException("credential-unavailable", "The configured credential reference cannot be resolved.");
    }
}

internal sealed class TokenMaterial
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Scope { get; set; }
}

internal sealed class OAuthProvider(IConnectionCredentialProvider credentials, HttpClient? client = null)
{
    // No redirects: credentials cannot follow an untrusted endpoint redirect.
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
    private HttpClient Http => client ?? DefaultHttp;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> managers = new();

    internal async Task<OpenIdConnectConfiguration> Metadata(ConnectionProfile profile, CancellationToken ct)
    {
        var endpoint = profile.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var manager = managers.GetOrAdd(endpoint, url => new ConfigurationManager<OpenIdConnectConfiguration>(url,
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(Http) { RequireHttps = true }));
        var metadata = await manager.GetConfigurationAsync(ct);
        RequireHttps(metadata.AuthorizationEndpoint);
        RequireHttps(metadata.TokenEndpoint);
        return metadata;
    }

    internal static void RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ConnectionException("invalid-configuration", "OAuth and resource endpoints must be absolute HTTPS URLs without user information or fragments.");
    }

    internal async Task<TokenMaterial> Exchange(ConnectionProfile p, Dictionary<string, string> fields, CancellationToken ct, bool child = false)
    {
        var metadata = await Metadata(p, ct);
        fields["client_id"] = child ? p.AgentIdentityClientId! : p.ClientId;
        if (!child)
            foreach (var item in await credentials.GetCredentialAsync(p, metadata.TokenEndpoint, ct)) fields[item.Key] = item.Value;
        using var body = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync(metadata.TokenEndpoint, body, ct);
        // Never include provider bodies in exceptions: they can echo assertions and authorization codes.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "provider-error";
            throw new ConnectionException(error is "invalid_grant" or "interaction_required" or "consent_required"
                ? "interaction-required" : "provider-denied", "The identity provider rejected token acquisition. Reconnect or review the configured consent and policy.");
        }
        if (!root.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new ConnectionException("provider-error", "The identity provider did not return an access token.");
        return new TokenMaterial {
            AccessToken = access.GetString()!,
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            IdToken = root.TryGetProperty("id_token", out var id) ? id.GetString() : null,
            Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetDouble() : 300)
        };
    }

    internal async Task<ClaimsPrincipal> Validate(ConnectionProfile p, string jwt, string audience, string? nonce, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jwt) || jwt.Length > 64 * 1024)
            throw new ConnectionException("invalid-assertion", "A bounded, signed identity token is required.");
        var metadata = await Metadata(p, ct);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(jwt, new TokenValidationParameters {
            ConfigurationManager = managers[p.Authority.TrimEnd('/') + "/.well-known/openid-configuration"],
            ValidIssuer = metadata.Issuer, ValidAudience = audience, IssuerSigningKeys = metadata.SigningKeys,
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RequireExpirationTime = true,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256]
        }, out _);
        if (nonce is not null && principal.FindFirst("nonce")?.Value != nonce)
            throw new ConnectionException("invalid-assertion", "Authorization nonce did not match.");
        return principal;
    }

    internal async Task<TokenMaterial> Acquire(ConnectionProfile p, ConnectionResource resource, TokenMaterial? grant, CancellationToken ct, TokenMaterial? previous = null)
    {
        var scopes = string.Join(' ', resource.Scopes);
        if (p.Authentication == ConnectionAuthentication.ClientCredentials)
            return await Exchange(p, new() { ["grant_type"] = "client_credentials", ["scope"] = scopes }, ct);
        if (p.Authentication == ConnectionAuthentication.AuthorizationCode)
        {
            if (grant?.RefreshToken is null) throw new ConnectionException("interaction-required", "Connect the account again to authorize this resource.");
            return await Exchange(p, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.RefreshToken, ["scope"] = scopes }, ct);
        }
        if (p.Authentication == ConnectionAuthentication.OnBehalfOf)
        {
            if (previous?.RefreshToken is { } refresh)
                return await Exchange(p, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = scopes }, ct);
            if (grant is null || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ConnectionException("interaction-required", "A current user assertion is required.");
            return await Exchange(p, new() {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["requested_token_use"] = "on_behalf_of",
                ["assertion"] = grant.AccessToken, ["scope"] = scopes
            }, ct);
        }
        // Agent ID uses the blueprint credential to obtain a federated child assertion.
        var parent = await Exchange(p, new() {
            ["grant_type"] = "client_credentials", ["scope"] = "api://AzureADTokenExchange/.default",
            ["fmi_path"] = p.AgentIdentityClientId!
        }, ct);
        var fields = new Dictionary<string, string> {
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = parent.AccessToken, ["scope"] = scopes
        };
        if (p.Authentication == ConnectionAuthentication.AgentIdApplication) fields["grant_type"] = "client_credentials";
        else if (previous?.RefreshToken is { } refresh)
        {
            fields["grant_type"] = "refresh_token";
            fields["refresh_token"] = refresh;
        }
        else
        {
            if (grant is null || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ConnectionException("interaction-required", "A current delegated user assertion is required.");
            fields["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer";
            fields["requested_token_use"] = "on_behalf_of";
            fields["assertion"] = grant.AccessToken;
        }
        return await Exchange(p, fields, ct, child: true);
    }
}
