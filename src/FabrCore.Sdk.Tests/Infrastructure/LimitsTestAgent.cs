using FabrCore.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Sdk.Tests.Infrastructure;

/// <summary>
/// A chat-client agent over a fake model whose configuration the test controls, exposing the resolved
/// compaction ladder and the proxy's own chat client so limit resolution can be asserted end to end.
/// </summary>
internal sealed class LimitsTestAgent : FabrCoreAgentProxy
{
    public const string Handle = "owner1:limits";

    private readonly IList<AITool>? tools;
    private ChatClientAgentResult? agent;

    private LimitsTestAgent(
        AgentConfiguration config,
        IServiceProvider services,
        IFabrCoreAgentHost host,
        IList<AITool>? tools) : base(config, services, host)
    {
        this.tools = tools;
    }

    public CompactionLadder? Ladder => CompactionLadderInfo;

    public Task<IChatClient> Client(string name = "default") => GetChatClient(name);

    /// <summary>Runs one user turn through the full proxy pipeline, run-safety scope included.</summary>
    public Task<AgentMessage> AskAsync(string message) => ((IFabrCoreAgentProxy)this).InternalOnMessage(new AgentMessage
    {
        FromHandle = "owner1",
        ToHandle = Handle,
        Kind = MessageKind.Request,
        Message = message
    });

    public override async Task OnInitialize()
        => agent = await CreateChatClientAgent(config.Models ?? "default", "main", tools);

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var run = await agent!.Agent.RunAsync(message.Message!, agent.Session);
        var response = message.Response();
        response.Message = run.Text;
        return response;
    }

    public static async Task<(LimitsTestAgent Agent, FakeChatClient Client)> CreateAsync(
        int? window,
        int? output = null,
        Dictionary<string, string>? args = null,
        Action<ModelConfiguration>? configureModel = null,
        FakeChatClient? client = null,
        IList<AITool>? tools = null,
        ModelContextLimitRegistry? registry = null)
    {
        client ??= FakeChatClient.WithTextResponse("Done.");
        var model = new ModelConfiguration
        {
            Name = "default",
            Provider = "Test",
            Uri = "http://localhost",
            Model = "test",
            ApiKeyAlias = "test",
            ContextWindowTokens = window,
            MaxOutputTokens = output
        };
        configureModel?.Invoke(model);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IFabrCoreChatClientService>(new FakeChatClientService(client) { ModelConfiguration = model });
        if (registry is not null)
        {
            services.AddSingleton(registry);
        }

        var agent = new LimitsTestAgent(
            new AgentConfiguration
            {
                Handle = Handle,
                AgentType = "limits-test",
                Models = "default",
                SystemPrompt = "You are a test agent.",
                Args = args ?? []
            },
            services.BuildServiceProvider(),
            new FakeAgentHost(Handle),
            tools);
        await agent.OnInitialize();
        return (agent, client);
    }
}
