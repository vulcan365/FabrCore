using Microsoft.Extensions.Configuration;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class AgentsSdkConfigurationBridgeTests
{
    private static ConfigurationManager CreateConfiguration(Dictionary<string, string?> values)
    {
        var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(values);
        return manager;
    }

    private static void Inject(ConfigurationManager config, Microsoft365CopilotOptions options) =>
        AgentsSdkConfigurationBridge.Inject(config, CopilotTestAgents.Resolve(options), config.GetSection("Microsoft365Copilot"));

    [TestMethod]
    public void SynthesizesServiceConnection_FromOptions()
    {
        var config = CreateConfiguration(new());
        var options = new Microsoft365CopilotOptions
        {
            TenantId = "tenant-456",
            Agents = { CopilotTestAgents.Entry(clientId: "client-123") },
        };

        Inject(config, options);

        Assert.AreEqual("ClientSecret", config["Connections:ServiceConnection:Settings:AuthType"]);
        Assert.AreEqual("client-123", config["Connections:ServiceConnection:Settings:ClientId"]);
        Assert.AreEqual("secret", config["Connections:ServiceConnection:Settings:ClientSecret"]);
        Assert.AreEqual("https://login.microsoftonline.com/tenant-456", config["Connections:ServiceConnection:Settings:AuthorityEndpoint"]);
        Assert.AreEqual("https://api.botframework.com/.default", config["Connections:ServiceConnection:Settings:Scopes:0"]);
        Assert.AreEqual("client-123", config["ConnectionsMap:0:Audience"]);
        Assert.AreEqual("*", config["ConnectionsMap:0:ServiceUrl"]);
        Assert.AreEqual("ServiceConnection", config["ConnectionsMap:0:Connection"]);
        Assert.AreEqual("true", config["AgentApplication:StartTypingTimer"]);
    }

    [TestMethod]
    public void EachBot_GetsItsOwnConnection_SelectedByTokenAudience()
    {
        var config = CreateConfiguration(new());
        var options = new Microsoft365CopilotOptions
        {
            TenantId = "tenant-default",
            Agents =
            {
                CopilotTestAgents.Entry("assistant", "client-a"),
                CopilotTestAgents.Entry("sales", "client-b"),
            },
        };
        options.Agents[1].TenantId = "tenant-sales";
        options.Agents[1].AuthType = "WorkloadIdentity";
        options.Agents[1].ClientSecret = null;
        options.Agents[1].FederatedTokenFile = "/var/run/token";

        Inject(config, options);

        Assert.AreEqual("client-a", config["Connections:ServiceConnection:Settings:ClientId"]);
        Assert.AreEqual("client-b", config["Connections:ServiceConnection-sales:Settings:ClientId"]);
        Assert.AreEqual("WorkloadIdentity", config["Connections:ServiceConnection-sales:Settings:AuthType"]);
        Assert.AreEqual("/var/run/token", config["Connections:ServiceConnection-sales:Settings:FederatedTokenFile"]);
        Assert.AreEqual("https://login.microsoftonline.com/tenant-sales", config["Connections:ServiceConnection-sales:Settings:AuthorityEndpoint"]);
        Assert.IsNull(config["Connections:ServiceConnection-sales:Settings:ClientSecret"]);

        Assert.AreEqual("client-a", config["ConnectionsMap:0:Audience"]);
        Assert.AreEqual("ServiceConnection", config["ConnectionsMap:0:Connection"]);
        Assert.AreEqual("client-b", config["ConnectionsMap:1:Audience"]);
        Assert.AreEqual("ServiceConnection-sales", config["ConnectionsMap:1:Connection"]);

        // The trailing entry has no audience: it is the SDK's default connection.
        Assert.IsNull(config["ConnectionsMap:2:Audience"]);
        Assert.AreEqual("ServiceConnection", config["ConnectionsMap:2:Connection"]);
    }

    [TestMethod]
    public void LeavesNativeConnectionsSection_Untouched()
    {
        var config = CreateConfiguration(new()
        {
            ["Connections:MyConn:Settings:ClientId"] = "native-client",
        });
        var options = new Microsoft365CopilotOptions { Agents = { CopilotTestAgents.Entry(clientId: "other-client") } };

        Inject(config, options);

        Assert.IsNull(config["Connections:ServiceConnection:Settings:ClientId"]);
        Assert.AreEqual("native-client", config["Connections:MyConn:Settings:ClientId"]);
    }

    [TestMethod]
    public void SkipsConnection_WhenNoClientId()
    {
        var config = CreateConfiguration(new());
        var options = new Microsoft365CopilotOptions { Agents = { CopilotTestAgents.Entry(clientId: null) } };

        Inject(config, options);

        Assert.IsNull(config["Connections:ServiceConnection:Settings:AuthType"]);
        Assert.IsNull(config["ConnectionsMap:0:ServiceUrl"]);
    }

    [TestMethod]
    public void ForwardsUserAuthorizationHandlers_ExcludingFabrCoreKeys()
    {
        var config = CreateConfiguration(new()
        {
            ["Microsoft365Copilot:UserAuthorization:PassUserTokenToAgent"] = "true",
            ["Microsoft365Copilot:UserAuthorization:DefaultHandlerName"] = "graph",
            ["Microsoft365Copilot:UserAuthorization:Handlers:graph:Settings:AzureBotOAuthConnectionName"] = "sso-conn",
            ["Microsoft365Copilot:UserAuthorization:Handlers:graph:Settings:OBOScopes:0"] = "https://graph.microsoft.com/.default",
        });
        var options = new Microsoft365CopilotOptions();

        Inject(config, options);

        Assert.AreEqual("graph", config["AgentApplication:UserAuthorization:DefaultHandlerName"]);
        Assert.AreEqual("sso-conn", config["AgentApplication:UserAuthorization:Handlers:graph:Settings:AzureBotOAuthConnectionName"]);
        Assert.AreEqual("https://graph.microsoft.com/.default", config["AgentApplication:UserAuthorization:Handlers:graph:Settings:OBOScopes:0"]);
        Assert.IsNull(config["AgentApplication:UserAuthorization:PassUserTokenToAgent"]);
    }

    [TestMethod]
    public void DoesNotForwardUserAuthorization_WhenNoHandlers()
    {
        var config = CreateConfiguration(new()
        {
            ["Microsoft365Copilot:UserAuthorization:PassUserTokenToAgent"] = "true",
        });
        var options = new Microsoft365CopilotOptions();

        Inject(config, options);

        Assert.IsFalse(config.GetSection("AgentApplication:UserAuthorization").Exists());
    }
}
