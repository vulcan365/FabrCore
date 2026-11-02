using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Connections.Tests;

/// <summary>A person connecting an account for agents that work without them, through a real host.</summary>
[TestClass, DoNotParallelize]
public sealed class ConnectionAuthorizationCodeTests
{
    private const string Tenant = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Alice = "aaaaaaaa-0000-0000-0000-00000000000a";
    private const string Bob = "aaaaaaaa-0000-0000-0000-00000000000b";
    private const string Authority = "https://identity.example";
    private const string App = "host-app";
    private const string Callback = "https://connect.example/connections/callback";
    private const string Reconnect = "https://connect.example/";

    private static readonly string AliceHandle = EntraPrincipalHandle.Create(Tenant, Alice)!;

    private static ConnectionProfile Template() => new()
    {
        Enabled = true,
        Authentication = ConnectionAuthentication.AuthorizationCode,
        Authority = Authority,
        ClientId = App,
        SignInScopes = ["openid", "offline_access", "https://graph.microsoft.com/Calendars.Read"],
        RedirectUris = [Callback],
        AllowedAgents = ["assistant"],
        Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/", Scopes = ["https://graph.microsoft.com/Calendars.Read"] } }
    };

    [TestMethod]
    public async Task APersonConnectsTheirOwnAccountAndTheirAgentUsesIt()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider(rsa);
        await using var app = await StartHostAsync(wire);
        var connections = app.Services.GetRequiredService<ConnectionService>();

        var (state, url) = await BeginAsync(connections, loginHint: "alice@example.com");
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        Assert.AreEqual("alice@example.com", query["login_hint"].ToString(), "The provider offers the account they're signed in with.");
        wire.SignsIn(Alice, query["nonce"]!);

        var status = await connections.Execute(AliceHandle, "complete", new("background", State: state, Code: "code"));
        Assert.AreEqual("connected", Read(status, "state"));
        Assert.AreEqual(Alice, Read(status, "externalSubject"), "Recorded by object id, as a chat's token records it.");

        var token = await app.Services.GetRequiredService<IConnectionService>().GetAccessTokenAsync(AliceHandle, $"{AliceHandle}:assistant", new(AliceHandle, "background"), "graph");
        Assert.IsFalse(string.IsNullOrEmpty(token.AccessToken));
    }

    [TestMethod]
    public async Task SigningInAsSomeoneElseConnectsNothing()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider(rsa);
        await using var app = await StartHostAsync(wire);
        var connections = app.Services.GetRequiredService<ConnectionService>();

        var (state, url) = await BeginAsync(connections);
        wire.SignsIn(Bob, QueryHelpers.ParseQuery(new Uri(url).Query)["nonce"]!);

        var refused = await Assert.ThrowsExactlyAsync<ConnectionException>(() => connections.Execute(AliceHandle, "complete", new("background", State: state, Code: "code")));
        Assert.AreEqual("access-denied", refused.Code);
        Assert.AreEqual("connection-required", Read(await connections.Execute(AliceHandle, "list", new("")), "state", index: 0));
    }

    [TestMethod]
    public async Task ALapsedConnectionIsToldOnceAndConnectingAgainClearsIt()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider(rsa);
        await using var app = await StartHostAsync(wire);
        var connections = app.Services.GetRequiredService<ConnectionService>();
        var service = app.Services.GetRequiredService<IConnectionService>();
        var agent = $"{AliceHandle}:assistant";

        await ConnectAsync(connections, wire);
        wire.RefusesRefresh = true;

        // The cached token from connecting is used first, so ask for one that needs the refresh token.
        var lapsed = await Assert.ThrowsExactlyAsync<ConnectionException>(() => service.GetAccessTokenAsync(AliceHandle, agent, new(AliceHandle, "background"), "calendar"));
        Assert.AreEqual("interaction-required", lapsed.Code);
        StringAssert.Contains(lapsed.Notice, Reconnect, "They're told where to connect it again.");
        Assert.AreEqual("interaction-required", Read(await connections.Execute(AliceHandle, "list", new("")), "state", index: 0));

        var again = await Assert.ThrowsExactlyAsync<ConnectionException>(() => service.GetAccessTokenAsync(AliceHandle, agent, new(AliceHandle, "background"), "calendar"));
        Assert.IsNull(again.Notice, "Once is enough.");

        // A client asks as it sends, and hands the notice to whoever made it, once a connection lapses there.
        wire.RefusesRefresh = false;
        await ConnectAsync(connections, wire);
        wire.RefusesRefresh = true;
        var told = new List<string>();
        using var client = await service.GetHttpClientAsync(AliceHandle, agent, new(AliceHandle, "background"), "calendar", notice => { told.Add(notice); return Task.CompletedTask; });
        await Assert.ThrowsExactlyAsync<ConnectionException>(() => client.GetAsync("v1.0/me/events"));
        Assert.AreEqual(1, told.Count);

        wire.RefusesRefresh = false;
        await ConnectAsync(connections, wire);
        Assert.AreEqual("connected", Read(await connections.Execute(AliceHandle, "list", new("")), "state", index: 0));
    }

    [TestMethod]
    public void TheHintIsTheAccountTheCallerSignedInWith()
    {
        Assert.AreEqual("alice@example.com", ConnectionsExtensions.LoginHint(new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice@example.com")], "test"))));
        Assert.AreEqual("hint", ConnectionsExtensions.LoginHint(new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice@example.com"), new Claim("login_hint", "hint")], "test"))));
        Assert.IsNull(ConnectionsExtensions.LoginHint(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static async Task ConnectAsync(ConnectionService connections, IdentityProvider wire)
    {
        var (state, url) = await BeginAsync(connections);
        wire.SignsIn(Alice, QueryHelpers.ParseQuery(new Uri(url).Query)["nonce"]!);
        await connections.Execute(AliceHandle, "complete", new("background", State: state, Code: "code"));
    }

    private static async Task<(string State, string Url)> BeginAsync(ConnectionService connections, string? loginHint = null)
    {
        using var json = JsonDocument.Parse(await connections.Execute(AliceHandle, "begin", new("background", RedirectUri: Callback, LoginHint: loginHint)));
        return (json.RootElement.GetProperty("state").GetString()!, json.RootElement.GetProperty("authorizationUrl").GetString()!);
    }

    private static string? Read(string json, string property, int? index = null)
    {
        using var document = JsonDocument.Parse(json);
        var element = index is { } i ? document.RootElement[i] : document.RootElement;
        return element.TryGetProperty(property, out var value) ? value.GetString() : null;
    }

    private static async Task<WebApplication> StartHostAsync(IdentityProvider wire)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:AdminAuthentication:ApiKey"] = "test-admin" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies = [typeof(ConnectionsExtensions).Assembly] }.UseConfigurationStore<ConnectionIntegrationTests.TestConfigurationStore>());
        builder.Services.AddFabrCoreConnections(o =>
        {
            o.Enabled = true;
            o.ReconnectUrl = Reconnect;
            var template = Template();
            template.Resources["calendar"] = new() { BaseUrl = "https://graph.microsoft.com/", Scopes = ["https://graph.microsoft.com/Calendars.ReadBasic"] };
            o.Templates["background"] = template;
        });
        builder.Services.AddSingleton(new OAuthProvider(new NoCredential(), new HttpClient(wire)));
        var app = builder.Build();
        app.UseFabrCoreServer();
        app.MapFabrCoreConnections();
        await app.StartAsync();
        return app;
    }

    private sealed class NoCredential : IConnectionCredentialProvider
    {
        public Task<Dictionary<string, string>> GetCredentialAsync(ConnectionProfile profile, string tokenEndpoint, CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, string>());
    }

    /// <summary>Signs in whoever the test says, and refuses a refresh token when told to, as Entra does after a password reset.</summary>
    private sealed class IdentityProvider(RSA rsa) : HttpMessageHandler
    {
        private readonly RSAParameters key = rsa.ExportParameters(false);
        private string? oid;
        private string? nonce;
        private int issued;

        public bool RefusesRefresh { get; set; }

        public void SignsIn(string objectId, string forNonce) => (oid, nonce) = (objectId, forNonce);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/keys")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { keys = new[] { new { kty = "RSA", kid = "key", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(key.Exponent!), n = Base64UrlEncoder.Encode(key.Modulus!) } } }) };
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { issuer = Authority, authorization_endpoint = $"{Authority}/authorize", token_endpoint = $"{Authority}/token", jwks_uri = $"{Authority}/keys" }) };

            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form["grant_type"] == "refresh_token" && RefusesRefresh)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            issued++;
            if (form["grant_type"] != "authorization_code")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = $"token-{issued}", refresh_token = $"refresh-{issued}", expires_in = 3600 }) };

            var idToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Authority, App, [new Claim("tid", Tenant), new Claim("oid", oid!), new Claim("sub", "pairwise-" + oid), new Claim("nonce", nonce!)],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(60),
                new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "key" }, SecurityAlgorithms.RsaSha256)));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = $"token-{issued}", refresh_token = $"refresh-{issued}", id_token = idToken, expires_in = 3600 }) };
        }
    }
}
