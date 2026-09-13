using System.Net;
using System.Net.Http.Json;
using FabrCore.Connections;
using Microsoft.AspNetCore.WebUtilities;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Connections.Tests;

[TestClass]
public sealed class OAuthProviderTests
{
    [TestMethod]
    public async Task IdentityValidationRequiresProviderSignatureAudienceAndNonce()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider { SigningParameters = rsa.ExportParameters(false) }; using var http = new HttpClient(wire);
        var provider = new OAuthProvider(new CredentialProvider(), http);
        var token = new JwtSecurityToken("https://identity.example", "parent", [new Claim("sub", "user"), new Claim("nonce", "nonce")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "key" }, SecurityAlgorithms.RsaSha256));
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        var profile = Profile(ConnectionAuthentication.AuthorizationCode);
        Assert.AreEqual("user", (await provider.Validate(profile, encoded, "parent", "nonce", default)).FindFirst("sub")?.Value);
        await Assert.ThrowsExactlyAsync<ConnectionException>(() => provider.Validate(profile, encoded, "parent", "wrong", default));
        await Assert.ThrowsExactlyAsync<SecurityTokenInvalidAudienceException>(() => provider.Validate(profile, encoded, "wrong", "nonce", default));
    }
    [TestMethod]
    public async Task ApplicationFlowRequestsConfiguredResourceWithoutUserGrant()
    {
        var wire = new IdentityProvider(); using var http = new HttpClient(wire);
        var provider = new OAuthProvider(new CredentialProvider(), http);
        var profile = Profile(ConnectionAuthentication.ClientCredentials);
        await provider.Acquire(profile, Resource(), null, default);
        Assert.AreEqual(1, wire.Forms.Count);
        Assert.AreEqual("client_credentials", wire.Forms[0]["grant_type"]);
        Assert.AreEqual("https://graph.microsoft.com/.default", wire.Forms[0]["scope"]);
        Assert.AreEqual("parent", wire.Forms[0]["client_id"]);
        Assert.AreEqual("credential", wire.Forms[0]["client_secret"]);
        Assert.IsFalse(wire.Forms[0].ContainsKey("assertion"));
    }
    [TestMethod]
    public async Task AgentIdExchangesParentAssertionForChildToken()
    {
        var wire = new IdentityProvider(); using var http = new HttpClient(wire);
        var provider = new OAuthProvider(new CredentialProvider(), http);
        await provider.Acquire(Profile(ConnectionAuthentication.AgentIdOnBehalfOf), Resource(), new() { AccessToken = "human", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }, default);
        Assert.AreEqual(2, wire.Forms.Count);
        Assert.AreEqual("child", wire.Forms[0]["fmi_path"]);
        Assert.AreEqual("api://AzureADTokenExchange/.default", wire.Forms[0]["scope"]);
        Assert.AreEqual("child", wire.Forms[1]["client_id"]);
        Assert.AreEqual("token-1", wire.Forms[1]["client_assertion"]);
        Assert.AreEqual("human", wire.Forms[1]["assertion"]);
        Assert.IsFalse(wire.Forms[1].ContainsKey("client_secret"));
    }
    [TestMethod]
    public async Task OboUsesItsResourceRefreshGrantAfterIncomingAssertionExpires()
    {
        var wire = new IdentityProvider(); using var http = new HttpClient(wire);
        var provider = new OAuthProvider(new CredentialProvider(), http);
        await provider.Acquire(Profile(ConnectionAuthentication.OnBehalfOf), Resource(), new() { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }, default, new() { RefreshToken = "refresh" });
        Assert.AreEqual("refresh_token", wire.Forms.Single()["grant_type"]);
        Assert.AreEqual("refresh", wire.Forms.Single()["refresh_token"]);
    }
    [TestMethod]
    public async Task ConsentDenialIsSanitizedAndNeverFallsBackToApplicationAuth()
    {
        var wire = new IdentityProvider { Deny = true }; using var http = new HttpClient(wire);
        var provider = new OAuthProvider(new CredentialProvider(), http);
        var error = await Assert.ThrowsExactlyAsync<ConnectionException>(() => provider.Acquire(Profile(ConnectionAuthentication.OnBehalfOf), Resource(), new() { AccessToken = "human", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }, default));
        Assert.AreEqual("interaction-required", error.Code); Assert.IsFalse(error.ToString().Contains("sensitive-provider-body"));
        Assert.AreEqual(1, wire.Forms.Count); Assert.AreEqual("on_behalf_of", wire.Forms[0]["requested_token_use"]);
    }
    private static ConnectionProfile Profile(ConnectionAuthentication authentication) => new() { Authentication = authentication, Authority = "https://identity.example", ClientId = "parent", AgentIdentityClientId = "child" };
    private static ConnectionResource Resource() => new() { BaseUrl = "https://graph.microsoft.com/", Scopes = ["https://graph.microsoft.com/.default"] };
    private sealed class CredentialProvider : IConnectionCredentialProvider
    {
        public Task<Dictionary<string, string>> GetCredentialAsync(ConnectionProfile profile, string tokenEndpoint, CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, string> { ["client_secret"] = "credential" });
    }
    private sealed class IdentityProvider : HttpMessageHandler
    {
        public RSAParameters? SigningParameters { get; init; }
        public bool Deny { get; init; }
        public List<Dictionary<string, string>> Forms { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/keys" && SigningParameters is { } key)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { keys = new[] { new { kty = "RSA", kid = "key", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(key.Exponent!), n = Base64UrlEncoder.Encode(key.Modulus!) } } }) };
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { issuer = "https://identity.example", authorization_endpoint = "https://identity.example/authorize", token_endpoint = "https://identity.example/token", jwks_uri = SigningParameters is null ? null : "https://identity.example/keys" }) };
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Value.ToString()); Forms.Add(fields);
            return Deny ? new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant", error_description = "sensitive-provider-body" }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "token-" + Forms.Count, expires_in = 3600 }) };
        }
    }
}
