using FabrCore.Host.A2A;
using FabrCore.Host.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests.A2A;

/// <summary>
/// The seam an integrating application most needs to test: which agent grain a call actually lands
/// in. Matching configuration fields does not answer that — the principal and the handle both feed
/// the grain key, and a custom resolver decides the principal.
/// </summary>
[TestClass]
public sealed class A2ACustomPrincipalResolverTests
{
    /// <summary>
    /// A resolver that consults a store, which is what any real per-user mapping does. It awaits
    /// on the request path rather than caching a snapshot behind a lock.
    /// </summary>
    private sealed class DirectoryPrincipalResolver : IA2APrincipalResolver
    {
        private readonly Dictionary<string, string> _directory;

        public DirectoryPrincipalResolver(Dictionary<string, string> directory) => _directory = directory;

        public int Lookups { get; private set; }

        public async ValueTask<string?> ResolvePrincipalHandleAsync(
            HttpContext context,
            A2AExposedAgent agent,
            string contextId,
            CancellationToken cancellationToken = default)
        {
            Lookups++;

            // Stand-in for the directory call a real resolver makes.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            var caller = context.Request.Headers["x-on-behalf-of"].ToString();
            return _directory.TryGetValue(caller, out var handle) ? handle : "a2a-copilot-studio";
        }

        public string? DescribeCaller(HttpContext context)
            => context.Request.Headers["x-on-behalf-of"].ToString() is { Length: > 0 } c ? c : null;
    }

    private static Dictionary<string, string?> Config() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:Agents:0:Name"] = "service-assistant",
        ["A2A:Agents:0:AgentType"] = "botanical-agent",
        ["A2A:Agents:0:Handle"] = "service-assistant",
    };

    private static Task<FabrCoreA2ATestHost> StartAsync(DirectoryPrincipalResolver resolver)
        => FabrCoreA2ATestHost.StartAsync(
            Config(),
            registry: new FakeFabrCoreRegistry().WithAgentType("botanical-agent", "Answers plant questions."),
            // Before the host's own registration, which uses TryAdd.
            configureServices: services => services.AddSingleton<IA2APrincipalResolver>(resolver));

    private static async Task<HttpResponseMessage> CallAsync(
        FabrCoreA2ATestHost host, string? onBehalfOf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/service-assistant")
        {
            Content = new StringContent(
                FabrCoreA2ATestHost.MessageSendRequest("hello"),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        if (onBehalfOf is not null)
        {
            request.Headers.Add("x-on-behalf-of", onBehalfOf);
        }

        return await host.Client.SendAsync(request);
    }

    [TestMethod]
    public async Task ACustomResolverDecidesWhichGrainTheTurnLandsIn()
    {
        var resolver = new DirectoryPrincipalResolver(new() { ["eric@vulcan365.com"] = "eric" });
        await using var host = await StartAsync(resolver);

        (await CallAsync(host, "eric@vulcan365.com")).EnsureSuccessStatusCode();

        // Principal and handle together are the grain key. Asserting the handle alone would pass
        // even when the caller reached an entirely different agent.
        var send = host.AgentService.Sends.Single();
        Assert.AreEqual("eric", send.Principal);
        Assert.AreEqual("service-assistant", send.Handle);
        Assert.AreEqual(1, resolver.Lookups);
    }

    [TestMethod]
    public async Task AnUnmappedCallerFallsBackToTheServicePrincipal()
    {
        var resolver = new DirectoryPrincipalResolver(new() { ["eric@vulcan365.com"] = "eric" });
        await using var host = await StartAsync(resolver);

        (await CallAsync(host, "stranger@example.com")).EnsureSuccessStatusCode();

        Assert.AreEqual("a2a-copilot-studio", host.AgentService.Sends.Single().Principal);
    }

    [TestMethod]
    public async Task TwoCallersReachTwoDifferentGrains()
    {
        var resolver = new DirectoryPrincipalResolver(new()
        {
            ["eric@vulcan365.com"] = "eric",
            ["dana@vulcan365.com"] = "dana",
        });
        await using var host = await StartAsync(resolver);

        (await CallAsync(host, "eric@vulcan365.com")).EnsureSuccessStatusCode();
        (await CallAsync(host, "dana@vulcan365.com")).EnsureSuccessStatusCode();

        CollectionAssert.AreEquivalent(
            new[] { "eric", "dana" },
            host.AgentService.Sends.Select(s => s.Principal).ToArray());

        // Each principal provisions its own instance of the agent.
        CollectionAssert.AreEquivalent(
            new[] { "eric", "dana" },
            host.AgentService.Ensured.Select(e => e.Principal).ToArray());
    }

    [TestMethod]
    public async Task TheResolverSuppliesTheCallerLabelStampedOnTheAgentMessage()
    {
        var resolver = new DirectoryPrincipalResolver(new() { ["eric@vulcan365.com"] = "eric" });
        await using var host = await StartAsync(resolver);

        (await CallAsync(host, "eric@vulcan365.com")).EnsureSuccessStatusCode();

        Assert.AreEqual(
            "eric@vulcan365.com",
            host.AgentService.Sends.Single().Message.Args!["A2A:Caller"]);
    }

    [TestMethod]
    public async Task ARejectingResolverStopsTheCallBeforeItReachesAnAgent()
    {
        var resolver = new RejectingResolver();
        await using var host = await FabrCoreA2ATestHost.StartAsync(
            Config(),
            registry: new FakeFabrCoreRegistry().WithAgentType("botanical-agent", "Answers plant questions."),
            configureServices: services => services.AddSingleton<IA2APrincipalResolver>(resolver));

        var response = await CallAsync(host, null);

        response.EnsureSuccessStatusCode();
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(-32600, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(0, host.AgentService.Sends.Count);
    }

    private sealed class RejectingResolver : IA2APrincipalResolver
    {
        public ValueTask<string?> ResolvePrincipalHandleAsync(
            HttpContext context, A2AExposedAgent agent, string contextId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);

        public string? DescribeCaller(HttpContext context) => null;
    }
}
