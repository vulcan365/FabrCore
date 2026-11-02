using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Streaming;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Host.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;

namespace FabrCore.Host.Tests;

/// <summary>
/// With the ACL enforcing, a caller acting as one principal can't reach another principal's agent by any way of naming it: the
/// route, the message body, either form of an event, or a WebSocket session. A host's API key gets no further than the principal it
/// names, so a host that keeps delegated tokens in agents depends on this.
/// </summary>
[TestClass, DoNotParallelize]
public sealed class CrossPrincipalAccessTests
{
    private const string Victim = "bob:agent";

    private static WebApplication _app = null!;

    private static IFabrCoreAgentService Agents => _app.Services.GetRequiredService<IFabrCoreAgentService>();

    private static IPrincipalGrain Alice => _app.Services.GetRequiredService<IClusterClient>().GetGrain<IPrincipalGrain>("alice");

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions().UseConfigurationStore<TestConfigurationStore>());

        // A host with no database doesn't enforce the ACL at all. This is the real evaluator a host with one uses, with no grants.
        var acl = new FabrCoreAclOptions { Mode = AclEnforcementMode.Enforce };
        builder.Services.AddSingleton<IAclEvaluator>(new AclEvaluator(new NoGrants(acl), Options.Create(acl), NullLogger<AclEvaluator>.Instance));
        _app = builder.Build();
        _app.UseFabrCoreServer();
        await _app.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [TestMethod]
    public async Task TheRouteCantNameAnotherPrincipalsAgent()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Agents.SendAndReceiveMessageAsync("alice", Victim, "hello"));

    [TestMethod]
    public async Task TheBodyCantNameAnotherPrincipalsAgent()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Alice.SendAndReceiveMessage(new AgentMessage { FromHandle = "alice", ToHandle = Victim, Message = "hello" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Alice.SendMessage(new AgentMessage { FromHandle = "alice", ToHandle = Victim, Message = "hello" }));
    }

    [TestMethod]
    public async Task AnEventCantReachAnotherPrincipalsAgentThroughItsRoute()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Agents.SendEventAsync("alice", Victim, new EventMessage { Type = "ping" }));

    [TestMethod]
    [DataRow(null)]
    [DataRow(StreamConstants.AgentEventNamespace)]
    [DataRow("agentevent")]
    public async Task AnEventCantReachAnotherPrincipalsAgentThroughItsBody(string? @namespace)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Agents.SendEventAsync("alice", "assistant", new EventMessage { Namespace = "AgentEvent", Channel = Victim, Type = "ping" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Alice.SendEvent(new EventMessage { Namespace = @namespace, Channel = Victim, Type = "ping" }));
    }

    [TestMethod]
    public async Task AWebSocketSessionActsOnlyAsThePrincipalItsTicketNamed()
    {
        var tickets = _app.Services.GetRequiredService<IWebSocketTicketService>();
        var ticket = await tickets.IssueAsync("alice");
        Assert.IsFalse(string.IsNullOrEmpty(ticket.Ticket));

        var dispatcher = new WebSocketOperationDispatcher(Alice, "alice");
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new EventMessage { Channel = Victim, Type = "ping" }, FabrCore.Core.WebSockets.FabrCoreWebSocketProtocol.JsonOptions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.DispatchAsync(new FabrCore.Core.WebSockets.FabrCoreWebSocketFrame
        {
            Type = "request",
            Operation = FabrCore.Core.WebSockets.FabrCoreWebSocketOperations.EventSend,
            Payload = payload
        }));
    }

    [TestMethod]
    public async Task ASharedTopicStillWorks()
        => await Alice.SendEvent(new EventMessage { Namespace = "shared-topic", Channel = "news", Type = "ping" });

    private sealed class NoGrants(FabrCoreAclOptions options) : IAclSnapshotProvider
    {
        public AclSnapshot Current { get; } = new(new AclSnapshotData(), options.AllPrincipalsGroupId, options.AllAgentsGroupId);
    }

    public sealed class TestConfigurationStore : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites => false;

        public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new FabrCoreConfiguration
            {
                ModelConfigurations =
                [
                    new() { Name = "default", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" },
                    new() { Name = "embeddings", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" }
                ]
            });

        public Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
