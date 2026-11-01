using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotAgentCatalogTests
{
    private static Microsoft365CopilotOptions Two() => new()
    {
        TenantId = "tenant-default",
        AuthType = "WorkloadIdentity",
        WelcomeMessage = "Hello from the host.",
        Manifest = { Name = "Host Default", DeveloperName = "Contoso", PublicHostName = "agents.contoso.com" },
        Agents =
        {
            CopilotTestAgents.Entry("assistant", CopilotTestAgents.ClientId, "chat-agent", route: "/api/messages"),
            CopilotTestAgents.Entry("sales", CopilotTestAgents.SecondClientId, "sales-agent"),
        },
    };

    [TestMethod]
    public void NoAgents_IsAValidEmptyCatalog()
    {
        var catalog = CopilotTestAgents.Catalog(new Microsoft365CopilotOptions());

        Assert.IsEmpty(catalog.Agents);
        Assert.IsNull(catalog.Find("assistant"));
        Assert.IsNull(catalog.Resolve(new ClaimsIdentity()));
    }

    [TestMethod]
    public void EachEntry_GetsItsOwnRouteHandleAndConnection()
    {
        var agents = CopilotTestAgents.Resolve(Two());

        Assert.AreEqual("/api/messages", agents[0].Route);
        Assert.AreEqual("assistant", agents[0].Handle);
        Assert.AreEqual("ServiceConnection", agents[0].ConnectionName);

        Assert.AreEqual("/api/messages/sales", agents[1].Route, "An entry without a route is served under its name.");
        Assert.AreEqual("sales", agents[1].Handle, "The handle defaults to the entry name.");
        Assert.AreEqual("sales-agent", agents[1].AgentType);
        Assert.AreEqual("ServiceConnection-sales", agents[1].ConnectionName);
    }

    [TestMethod]
    public void TopLevelSettings_AreDefaults_AnEntryCanOverride()
    {
        var options = Two();
        options.Agents[1].TenantId = "tenant-sales";
        options.Agents[1].AuthType = "ClientSecret";
        options.Agents[1].WelcomeMessage = string.Empty;
        options.Agents[1].Manifest.Name = "Sales Desk";
        options.Agents[1].Manifest.Description = "Answers sales questions.";

        var agents = CopilotTestAgents.Resolve(options);

        Assert.AreEqual("tenant-default", agents[0].TenantId);
        Assert.AreEqual("WorkloadIdentity", agents[0].AuthType);
        Assert.AreEqual("Hello from the host.", agents[0].WelcomeMessage);
        Assert.AreEqual("Contoso", agents[0].Manifest.DeveloperName);

        Assert.AreEqual("tenant-sales", agents[1].TenantId);
        Assert.AreEqual("ClientSecret", agents[1].AuthType);
        Assert.AreEqual(string.Empty, agents[1].WelcomeMessage, "An empty greeting turns it off for that agent.");
        Assert.AreEqual("Sales Desk", agents[1].Manifest.Name);
        Assert.AreEqual("Answers sales questions.", agents[1].Manifest.Description);
        Assert.AreEqual("Contoso", agents[1].Manifest.DeveloperName);
        Assert.AreEqual("agents.contoso.com", agents[1].Manifest.PublicHostName);
    }

    [TestMethod]
    public void ManifestName_ComesFromTheHostForOneAgent_AndFromTheEntryForSeveral()
    {
        var single = new Microsoft365CopilotOptions { Manifest = { Name = "Host Default" }, Agents = { CopilotTestAgents.Entry() } };

        Assert.AreEqual("Host Default", CopilotTestAgents.Resolve(single)[0].Manifest.Name);
        CollectionAssert.AreEqual(
            new[] { "assistant", "sales" },
            CopilotTestAgents.Resolve(Two()).Select(agent => agent.Manifest.Name).ToArray(),
            "Two packages cannot both carry the host's default name.");
    }

    [TestMethod]
    [DataRow("", "Name is required")]
    [DataRow("has space", "Name is required")]
    [DataRow("a/b", "Name is required")]
    public void RejectsUnusableNames(string name, string expected)
    {
        var options = new Microsoft365CopilotOptions { Agents = { CopilotTestAgents.Entry(name) } };

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CopilotTestAgents.Resolve(options));

        StringAssert.Contains(error.Message, expected);
        StringAssert.Contains(error.Message, "Microsoft365Copilot:Agents:0");
    }

    [TestMethod]
    public void RejectsWhatTwoAgentsCannotShare()
    {
        static string Error(Action<Microsoft365CopilotOptions> change)
        {
            var options = Two();
            change(options);
            return Assert.ThrowsExactly<InvalidOperationException>(() => CopilotTestAgents.Resolve(options)).Message;
        }

        StringAssert.Contains(Error(o => o.Agents[1].Name = "ASSISTANT"), "used by more than one agent");
        StringAssert.Contains(Error(o => o.Agents[1].Route = "/api/messages/"), "Each Azure Bot needs its own messaging endpoint");
        StringAssert.Contains(Error(o => o.Agents[1].ClientId = CopilotTestAgents.ClientId), "its own Azure Bot registration");
        StringAssert.Contains(Error(o => o.Agents[1].Manifest.Name = "Assistant"), "its own name");
    }

    [TestMethod]
    public void RejectsAnEntryThatCannotReachAnAgent()
    {
        static string Error(Action<CopilotAgentEntryOptions> change)
        {
            var options = new Microsoft365CopilotOptions { Agents = { CopilotTestAgents.Entry() } };
            change(options.Agents[0]);
            return Assert.ThrowsExactly<InvalidOperationException>(() => CopilotTestAgents.Resolve(options)).Message;
        }

        StringAssert.Contains(Error(e => e.AgentType = " "), "AgentType is required");
        StringAssert.Contains(Error(e => e.Handle = "system:assistant"), "bare agent handle");
        StringAssert.Contains(Error(e => e.Route = "/api/messages/{handle}"), "fixed path");
    }

    [TestMethod]
    public void ATurn_IsMatchedByItsRoute_ThenByItsBot_ThenByBeingTheOnlyAgent()
    {
        var catalog = CopilotTestAgents.Catalog(Two());

        var routed = new ClaimsIdentity([
            new Claim(CopilotAgentCatalog.AgentClaimType, "sales"),
            new Claim("aud", CopilotTestAgents.ClientId)]);
        Assert.AreEqual("sales", catalog.Resolve(routed)!.Name, "The route's own claim wins.");

        var byAudience = new ClaimsIdentity([new Claim("aud", CopilotTestAgents.SecondClientId)]);
        Assert.AreEqual("sales", catalog.Resolve(byAudience)!.Name);

        Assert.IsNull(catalog.Resolve(new ClaimsIdentity()), "With several agents an unmarked turn belongs to none of them.");
        Assert.AreEqual("assistant", CopilotTestAgents.Catalog(CopilotTestAgents.Single()).Resolve(null)!.Name);
    }

    [TestMethod]
    public void ARoute_AcceptsOnlyATokenIssuedForItsOwnBot()
    {
        var options = Two();
        var agents = CopilotTestAgents.Resolve(options);

        static DefaultHttpContext Request(string? audience) => new()
        {
            User = new ClaimsPrincipal(audience is null
                ? new ClaimsIdentity()
                : new ClaimsIdentity([new Claim("aud", audience)], "test"))
        };

        var own = Request(CopilotTestAgents.SecondClientId);
        Assert.IsTrue(CopilotAgentRouting.TryBind(own, agents[1], options));
        Assert.AreEqual("sales", own.User.FindFirst(CopilotAgentCatalog.AgentClaimType)!.Value);

        Assert.IsFalse(
            CopilotAgentRouting.TryBind(Request(CopilotTestAgents.ClientId), agents[1], options),
            "A token for the assistant's bot must not be accepted on the sales route.");
        Assert.IsFalse(CopilotAgentRouting.TryBind(Request(null), agents[1], options));

        options.TokenValidation.Audiences.Add("api://shared");
        Assert.IsTrue(CopilotAgentRouting.TryBind(Request("api://shared"), agents[1], options));
    }

    [TestMethod]
    public void AnonymousDevelopment_StillNamesTheAgentAndItsBot()
    {
        var options = Two();
        options.TokenValidation.Enabled = false;
        var sales = CopilotTestAgents.Resolve(options)[1];
        var context = new DefaultHttpContext();

        Assert.IsTrue(CopilotAgentRouting.TryBind(context, sales, options));

        var identity = (ClaimsIdentity)context.User.Identity!;
        Assert.AreEqual("sales", identity.FindFirst(CopilotAgentCatalog.AgentClaimType)!.Value);
        Assert.AreEqual(CopilotTestAgents.SecondClientId, identity.FindFirst("aud")!.Value,
            "Without a token the Agents SDK still needs to know which bot to answer as.");
    }
}
