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

    [TestMethod]
    public void SynthesizesServiceConnection_FromOptions()
    {
        var config = CreateConfiguration(new()
        {
            ["Microsoft365Copilot:ClientId"] = "client-123",
        });
        var options = new Microsoft365CopilotOptions
        {
            ClientId = "client-123",
            TenantId = "tenant-456",
            ClientSecret = "secret",
        };

        AgentsSdkConfigurationBridge.Inject(config, options, config.GetSection("Microsoft365Copilot"));

        Assert.AreEqual("ClientSecret", config["Connections:ServiceConnection:Settings:AuthType"]);
        Assert.AreEqual("client-123", config["Connections:ServiceConnection:Settings:ClientId"]);
        Assert.AreEqual("secret", config["Connections:ServiceConnection:Settings:ClientSecret"]);
        Assert.AreEqual("https://login.microsoftonline.com/tenant-456", config["Connections:ServiceConnection:Settings:AuthorityEndpoint"]);
        Assert.AreEqual("https://api.botframework.com/.default", config["Connections:ServiceConnection:Settings:Scopes:0"]);
        Assert.AreEqual("*", config["ConnectionsMap:0:ServiceUrl"]);
        Assert.AreEqual("ServiceConnection", config["ConnectionsMap:0:Connection"]);
        Assert.AreEqual("true", config["AgentApplication:StartTypingTimer"]);
    }

    [TestMethod]
    public void LeavesNativeConnectionsSection_Untouched()
    {
        var config = CreateConfiguration(new()
        {
            ["Connections:MyConn:Settings:ClientId"] = "native-client",
        });
        var options = new Microsoft365CopilotOptions { ClientId = "other-client", ClientSecret = "s" };

        AgentsSdkConfigurationBridge.Inject(config, options, config.GetSection("Microsoft365Copilot"));

        Assert.IsNull(config["Connections:ServiceConnection:Settings:ClientId"]);
        Assert.AreEqual("native-client", config["Connections:MyConn:Settings:ClientId"]);
    }

    [TestMethod]
    public void SkipsConnection_WhenNoClientId()
    {
        var config = CreateConfiguration(new());
        var options = new Microsoft365CopilotOptions();

        AgentsSdkConfigurationBridge.Inject(config, options, config.GetSection("Microsoft365Copilot"));

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

        AgentsSdkConfigurationBridge.Inject(config, options, config.GetSection("Microsoft365Copilot"));

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

        AgentsSdkConfigurationBridge.Inject(config, options, config.GetSection("Microsoft365Copilot"));

        Assert.IsFalse(config.GetSection("AgentApplication:UserAuthorization").Exists());
    }
}
