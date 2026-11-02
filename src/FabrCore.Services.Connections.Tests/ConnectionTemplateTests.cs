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
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Connections.Tests;

[TestClass, DoNotParallelize]
public sealed class ConnectionTemplateTests
{
    private const string Tenant = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Alice = "aaaaaaaa-0000-0000-0000-00000000000a";
    private const string Bob = "aaaaaaaa-0000-0000-0000-00000000000b";
    private const string Authority = "https://identity.example";
    private const string App = "bot-app";

    private static ConnectionProfile Template(string baseUrl = "https://graph.microsoft.com/v1.0/") => new()
    {
        Enabled = true,
        Authentication = ConnectionAuthentication.OnBehalfOf,
        Authority = Authority,
        ClientId = App,
        CredentialReference = "host",
        AssertionAudiences = [$"api://botid-{App}"],
        AllowedAgents = ["assistant"],
        Resources = new() { ["graph"] = new() { BaseUrl = baseUrl, Scopes = ["https://graph.microsoft.com/.default"] } }
    };

    // ---- Per person, from one template

    [TestMethod]
    public void EachPersonGetsTheirOwnConnectionNamingOnlyTheirOwnAgent()
    {
        var alice = new Dictionary<string, ConnectionRecord>();
        var bob = new Dictionary<string, ConnectionRecord>();
        var templates = new Dictionary<string, ConnectionProfile> { ["work"] = Template() };

        Assert.IsTrue(ConnectionGrain.ApplyTemplates(alice, templates, "alice"));
        Assert.IsTrue(ConnectionGrain.ApplyTemplates(bob, templates, "bob"));

        CollectionAssert.AreEqual(new[] { "alice:assistant" }, alice["work"].Profile.AllowedAgents);
        CollectionAssert.AreEqual(new[] { "bob:assistant" }, bob["work"].Profile.AllowedAgents);
        Assert.AreEqual("work", alice["work"].Profile.Name);
        CollectionAssert.AreEqual(new[] { "assistant" }, templates["work"].AllowedAgents, "The template itself doesn't change.");
    }

    [TestMethod]
    public void AnUnchangedTemplateLeavesThePersonsSignInAlone()
    {
        var records = new Dictionary<string, ConnectionRecord>();
        var templates = new Dictionary<string, ConnectionProfile> { ["work"] = Template() };
        ConnectionGrain.ApplyTemplates(records, templates, "alice");
        records["work"].Grant = new() { AccessToken = "kept" };

        Assert.IsFalse(ConnectionGrain.ApplyTemplates(records, templates, "alice"));

        Assert.AreEqual("kept", records["work"].Grant!.AccessToken);
    }

    [TestMethod]
    public void AChangedTemplateEndsThePersonsSignIn()
    {
        var records = new Dictionary<string, ConnectionRecord>();
        ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile> { ["work"] = Template() }, "alice");
        records["work"].Grant = new() { AccessToken = "old" };
        records["work"].Tokens["graph"] = new() { AccessToken = "old", RefreshToken = "old" };

        Assert.IsTrue(ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile> { ["work"] = Template("https://graph.microsoft.com/beta/") }, "alice"));

        Assert.IsNull(records["work"].Grant);
        Assert.IsEmpty(records["work"].Tokens);
        Assert.AreEqual("https://graph.microsoft.com/beta/", records["work"].Profile.Resources["graph"].BaseUrl);
    }

    [TestMethod]
    public void ARemovedTemplateTakesThePersonsTokensWithIt()
    {
        var records = new Dictionary<string, ConnectionRecord>();
        ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile> { ["work"] = Template() }, "alice");
        records["work"].Tokens["graph"] = new() { AccessToken = "old", RefreshToken = "old" };

        Assert.IsTrue(ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile>(), "alice"));

        Assert.IsFalse(records.ContainsKey("work"));
    }

    [TestMethod]
    public void AProfileAnAdministratorSavedIsNotReplaced()
    {
        var saved = new ConnectionRecord { Profile = new() { Name = "work", ClientId = "theirs" }, Grant = new() { AccessToken = "kept" } };
        var records = new Dictionary<string, ConnectionRecord> { ["work"] = saved };

        Assert.IsFalse(ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile> { ["work"] = Template() }, "alice"));
        Assert.IsFalse(ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile>(), "alice"));

        Assert.AreSame(saved, records["work"]);
    }

    // ---- What a template can be

    [TestMethod]
    public void ATemplateThatCantBeUsedStopsTheHostStarting()
    {
        void Register(Action<ConnectionProfile> change)
        {
            var template = Template();
            change(template);
            new ServiceCollection().AddFabrCoreConnections(o => { o.Enabled = true; o.Templates["work"] = template; });
        }

        Register(_ => { });
        Assert.ThrowsExactly<InvalidOperationException>(() => Register(t => t.Authentication = ConnectionAuthentication.ClientCredentials));
        Assert.ThrowsExactly<InvalidOperationException>(() => Register(t => t.AllowedAgents = ["alice:assistant"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => Register(t => t.AllowedAgents = []));
        Assert.ThrowsExactly<InvalidOperationException>(() => Register(t => t.Authority = "http://not-secure"));
    }

    [TestMethod]
    public void TemplatesBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FabrCore:Connections:Enabled"] = "true",
            ["FabrCore:Connections:Templates:work:Enabled"] = "true",
            ["FabrCore:Connections:Templates:work:Authentication"] = "OnBehalfOf",
            ["FabrCore:Connections:Templates:work:Authority"] = Authority,
            ["FabrCore:Connections:Templates:work:ClientId"] = App,
            ["FabrCore:Connections:Templates:work:CredentialReference"] = "host",
            ["FabrCore:Connections:Templates:work:AllowedAgents:0"] = "assistant",
            ["FabrCore:Connections:Templates:work:Resources:graph:BaseUrl"] = "https://graph.microsoft.com/v1.0/",
            ["FabrCore:Connections:Templates:work:Resources:graph:Scopes:0"] = "https://graph.microsoft.com/.default",
        }).Build();
        var services = new ServiceCollection();

        services.AddFabrCoreConnections(configuration);

        var options = (ConnectionsOptions)services.Single(d => d.ServiceType == typeof(ConnectionsOptions)).ImplementationInstance!;
        Assert.AreEqual(App, options.Templates["work"].ClientId);
        Assert.AreEqual("assistant", options.Templates["work"].AllowedAgents.Single());
    }

    // ---- Managed identity instead of a secret

    [TestMethod]
    public async Task ACredentialCanBeAManagedIdentitysToken()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FabrCore:ConnectionCredentials:host:ManagedIdentityClientId"] = "identity-1"
        }).Build();
        string? askedFor = null;
        var provider = new ConfigurationCredentialProvider(configuration, (clientId, _) => { askedFor = clientId; return Task.FromResult("identity-token"); });

        var fields = await provider.GetCredentialAsync(new ConnectionProfile { ClientId = App, CredentialReference = "host" }, "https://identity.example/token", default);

        Assert.AreEqual("identity-1", askedFor);
        Assert.AreEqual("identity-token", fields["client_assertion"]);
        Assert.AreEqual("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", fields["client_assertion_type"]);
        Assert.IsFalse(fields.ContainsKey("client_secret"));
    }

    // ---- A person's token, through a real host

    [TestMethod]
    public async Task ASignedInPersonsTokenReachesOnlyTheirOwnConnectionForThatAgent()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider(rsa.ExportParameters(false));
        await using var app = await StartHostAsync(wire);
        var assertions = app.Services.GetRequiredService<IConnectionUserAssertions>();
        var service = app.Services.GetRequiredService<IConnectionService>();
        var connections = app.Services.GetRequiredService<ConnectionService>();
        var alice = EntraPrincipalHandle.Create(Tenant, Alice)!;
        var agent = $"{alice}:assistant";

        // First use: the person has the connection, not yet signed in.
        Assert.AreEqual("connection-required", Status(await connections.Execute(alice, "list", new(""))));
        var missing = await Assert.ThrowsExactlyAsync<ConnectionException>(() => service.GetAccessTokenAsync(alice, agent, new(alice, "work"), "graph"));
        Assert.AreEqual("interaction-required", missing.Code);

        // Another of their agents isn't one the connection allows, so it isn't given the token.
        await assertions.SubmitAsync(alice, $"{alice}:other", Token(rsa, Alice, $"api://botid-{App}", "first"));
        Assert.AreEqual("connection-required", Status(await connections.Execute(alice, "list", new(""))));

        // The bot's audience is accepted, and the token works for the agent.
        await assertions.SubmitAsync(alice, agent, Token(rsa, Alice, $"api://botid-{App}", "first"));
        Assert.AreEqual("connected", Status(await connections.Execute(alice, "list", new(""))));
        var token = await service.GetAccessTokenAsync(alice, agent, new(alice, "work"), "graph");
        Assert.AreEqual("token-1", token.AccessToken);

        // A later token that isn't needed yet doesn't clear what's cached.
        await assertions.SubmitAsync(alice, agent, Token(rsa, Alice, $"api://botid-{App}", "second"));
        Assert.AreEqual("token-1", (await service.GetAccessTokenAsync(alice, agent, new(alice, "work"), "graph")).AccessToken);
        Assert.AreEqual(1, wire.Forms.Count);

        // Someone else's token, and a token for another app, are refused.
        var someoneElse = await Assert.ThrowsExactlyAsync<ConnectionException>(() => assertions.SubmitAsync(alice, agent, Token(rsa, Bob, $"api://botid-{App}", "bob")));
        Assert.AreEqual("access-denied", someoneElse.Code);
        var wrongApp = await Assert.ThrowsExactlyAsync<ConnectionException>(() => assertions.SubmitAsync(alice, agent, Token(rsa, Alice, "api://botid-other", "other", expiresSoon: true)));
        Assert.AreEqual("invalid-assertion", wrongApp.Code);

        // Nobody can submit for a principal that isn't the one they're acting for.
        var bob = EntraPrincipalHandle.Create(Tenant, Bob)!;
        var asOther = await connections.Execute(bob, "list", new(""));
        Assert.AreEqual("connection-required", Status(asOther));
        await Assert.ThrowsExactlyAsync<ConnectionException>(() => service.GetAccessTokenAsync(alice, $"{bob}:assistant", new(alice, "work"), "graph"));
    }

    [TestMethod]
    public async Task ATokenCloseToExpiringIsReplaced()
    {
        using var rsa = RSA.Create(2048);
        var wire = new IdentityProvider(rsa.ExportParameters(false));
        await using var app = await StartHostAsync(wire);
        var assertions = app.Services.GetRequiredService<IConnectionUserAssertions>();
        var service = app.Services.GetRequiredService<IConnectionService>();
        var alice = EntraPrincipalHandle.Create(Tenant, Alice)!;
        var agent = $"{alice}:assistant";

        await assertions.SubmitAsync(alice, agent, Token(rsa, Alice, $"api://botid-{App}", "first", expiresSoon: true));
        await service.GetAccessTokenAsync(alice, agent, new(alice, "work"), "graph");
        await assertions.SubmitAsync(alice, agent, Token(rsa, Alice, $"api://botid-{App}", "second"));
        await service.GetAccessTokenAsync(alice, agent, new(alice, "work"), "graph");

        Assert.AreEqual(2, wire.Forms.Count);
        StringAssert.Contains(new JwtSecurityTokenHandler().ReadJwtToken(wire.Forms[1]["assertion"]).Id, "second");
    }

    private static string Status(string list) => JsonDocument.Parse(list).RootElement[0].GetProperty("state").GetString()!;

    private static string Token(RSA rsa, string oid, string audience, string id, bool expiresSoon = false)
    {
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(new JwtSecurityToken(
            Authority, audience,
            [new Claim("tid", Tenant), new Claim("oid", oid), new Claim("scp", "access_as_user"), new Claim("jti", id)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(expiresSoon ? 3 : 60),
            new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "key" }, SecurityAlgorithms.RsaSha256)));
    }

    private static async Task<WebApplication> StartHostAsync(IdentityProvider wire)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:AdminAuthentication:ApiKey"] = "test-admin" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies = [typeof(ConnectionsExtensions).Assembly] }.UseConfigurationStore<ConnectionIntegrationTests.TestConfigurationStore>());
        builder.Services.AddFabrCoreConnections(o => { o.Enabled = true; o.Templates["work"] = Template(); });
        builder.Services.AddSingleton(new OAuthProvider(new FixedCredential(), new HttpClient(wire)));
        var app = builder.Build();
        app.UseFabrCoreServer();
        app.MapFabrCoreConnections();
        await app.StartAsync();
        return app;
    }

    private sealed class FixedCredential : IConnectionCredentialProvider
    {
        public Task<Dictionary<string, string>> GetCredentialAsync(ConnectionProfile profile, string tokenEndpoint, CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, string> { ["client_assertion"] = "identity-token" });
    }

    private sealed class IdentityProvider(RSAParameters key) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/keys")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { keys = new[] { new { kty = "RSA", kid = "key", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(key.Exponent!), n = Base64UrlEncoder.Encode(key.Modulus!) } } }) };
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { issuer = Authority, authorization_endpoint = $"{Authority}/authorize", token_endpoint = $"{Authority}/token", jwks_uri = $"{Authority}/keys" }) };
            Forms.Add(QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Value.ToString()));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "token-" + Forms.Count, expires_in = 3600 }) };
        }
    }
}
