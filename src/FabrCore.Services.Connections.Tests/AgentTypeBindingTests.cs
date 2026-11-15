using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Connections;

namespace FabrCore.Services.Connections.Tests;

/// <summary>An application connection can allow every instance of an agent type, whoever owns it, and nothing else can.</summary>
[TestClass]
public sealed class AgentTypeBindingTests
{
    private static readonly ConnectionsOptions AgentId = new() { Enabled = true, EntraAgentIdEnabled = true };

    private static ConnectionProfile Application(ConnectionAuthentication authentication = ConnectionAuthentication.AgentIdApplication) => new()
    {
        Name = "tenant",
        Enabled = true,
        Authentication = authentication,
        Authority = "https://login.microsoftonline.com/tenant",
        ClientId = "blueprint",
        AgentIdentityClientId = "identity",
        CredentialReference = "host",
        AllowedAgentTypes = ["tenant-guardian"],
        Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/v1.0/", Scopes = ["https://graph.microsoft.com/.default"] } }
    };

    [TestMethod]
    public void AnyInstanceOfAnAllowedTypeMayUseIt()
    {
        var p = Application();
        ConnectionGrain.Authorize(p, "host", "guardian-admin-1", "guardian-admin-1:tenant-guardian", "tenant-guardian");
        ConnectionGrain.Authorize(p, "host", "guardian-admin-2", "guardian-admin-2:anything", "tenant-guardian");
    }

    [TestMethod]
    public void AnotherTypeOrNoTypeIsRefused()
    {
        var p = Application();
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.Authorize(p, "host", "alice", "alice:tenant-guardian", "guardian-responder"));
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.Authorize(p, "host", "alice", "alice:tenant-guardian"));
    }

    [TestMethod]
    public void ExactHandlesStillWorkBesideTypes()
    {
        var p = Application();
        p.AllowedAgents = ["guardian:guardian-responder"];
        ConnectionGrain.Authorize(p, "host", "guardian", "guardian:guardian-responder", "guardian-responder");
    }

    [TestMethod]
    public void ClientCredentialsCanAllowTypesToo()
    {
        var p = Application(ConnectionAuthentication.ClientCredentials);
        ConnectionGrain.ValidateProfile(p, AgentId);
        ConnectionGrain.Authorize(p, "host", "bob", "bob:x", "tenant-guardian");
    }

    [TestMethod]
    public void ADelegatedConnectionCantAllowTypes()
    {
        var p = Application(ConnectionAuthentication.OnBehalfOf);
        var e = Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.ValidateProfile(p, AgentId));
        Assert.AreEqual("invalid-configuration", e.Code);

        // And a type is never honoured on one, even if a stored profile carried it.
        p.AllowedAgents = [];
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.Authorize(p, "alice", "alice", "alice:tenant-guardian", "tenant-guardian"));
    }

    [TestMethod]
    public void TypesAreNamesNotHandles()
    {
        var p = Application();
        p.AllowedAgentTypes = ["alice:tenant-guardian"];
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.ValidateProfile(p, AgentId));
        p.AllowedAgentTypes = [" "];
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.ValidateProfile(p, AgentId));
        p.AllowedAgentTypes = null!;
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.ValidateProfile(p, AgentId));
    }

    [TestMethod]
    public void ATemplatesHashIsWhatItWasBeforeTypesExisted()
    {
        var template = new ConnectionProfile
        {
            Enabled = true, Authentication = ConnectionAuthentication.OnBehalfOf, Authority = "https://identity.example",
            ClientId = "bot-app", CredentialReference = "host", AllowedAgents = ["assistant"],
            Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/v1.0/", Scopes = ["https://graph.microsoft.com/.default"] } }
        };
        var before = JsonSerializer.Serialize(template, JsonSerializerOptions.Web).Replace(",\"allowedAgentTypes\":[]", "");
        Assert.DoesNotContain("allowedAgentTypes", before);
        var records = new Dictionary<string, ConnectionRecord>
        {
            ["work"] = new() { Profile = template, TemplateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before))), Grant = new() { AccessToken = "kept" } }
        };

        Assert.IsFalse(ConnectionGrain.ApplyTemplates(records, new Dictionary<string, ConnectionProfile> { ["work"] = template }, "alice"),
            "Upgrading mustn't end everyone's sign-in.");
        Assert.AreEqual("kept", records["work"].Grant!.AccessToken);
    }
}
