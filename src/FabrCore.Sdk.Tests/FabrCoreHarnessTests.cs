#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class FabrCoreHarnessTests
{
    private const string CoordinatorHandle = "owner1:coordinator";
    private const string SessionKey = "_harness_session:main";
    private const string CorruptKey = "_harness_session_corrupt:main";

    // ---------------------------------------------------------------------------------------------
    // Composition
    // ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ProvidersResolveThroughTheWholeDecoratorChain()
    {
        // TodoCompletionLoopEvaluator and BackgroundTaskCompletionLoopEvaluator find their providers via
        // AIAgent.GetService through LoopAgent -> OpenTelemetryAgent -> ChatClientAgent. If that forwarding
        // breaks, the loop can never observe completion and never terminates.
        var (agent, _, _) = CreateAgent(args: new Dictionary<string, string>
        {
            [HarnessArgs.BackgroundAgents] = "owner1:crm"
        });

        await agent.OnInitialize();

        Assert.IsNotNull(agent.Harness);
        Assert.IsNotNull(agent.Harness.Agent.GetService<TodoProvider>());
        Assert.IsNotNull(agent.Harness.Agent.GetService<BackgroundAgentsProvider>());
        Assert.IsTrue(agent.Harness.Agent.IsLooping);
    }

    [TestMethod]
    public async Task NoLoopIsComposedWhenTheLoopIsSwitchedOff()
    {
        var (agent, _, _) = CreateAgent(args: new Dictionary<string, string>
        {
            [HarnessArgs.Loop] = "none"
        });

        await agent.OnInitialize();

        Assert.IsFalse(agent.Harness!.Agent.IsLooping);
    }

    [TestMethod]
    public async Task TheHarnessPreambleAndTheAgentSystemPromptBothReachTheModel()
    {
        var chatClient = FakeChatClient.WithTextResponse("done");
        var (agent, _, _) = CreateAgent(chatClient, systemPrompt: "Always cite the runbook.");

        await agent.OnInitialize();
        await agent.OnMessage(Ask("Do the thing"));

        var instructions = chatClient.RequestOptions[0]?.Instructions ?? string.Empty;
        StringAssert.Contains(instructions, "Track multi-step work with the todo tools");
        StringAssert.Contains(instructions, "Always cite the runbook.");
    }

    [TestMethod]
    public async Task AnEmptyInstructionsArgDropsThePreambleButKeepsTheSystemPrompt()
    {
        var chatClient = FakeChatClient.WithTextResponse("done");
        var (agent, _, _) = CreateAgent(
            chatClient,
            args: new Dictionary<string, string> { [HarnessArgs.Instructions] = string.Empty },
            systemPrompt: "Always cite the runbook.");

        await agent.OnInitialize();
        await agent.OnMessage(Ask("Do the thing"));

        var instructions = chatClient.RequestOptions[0]?.Instructions ?? string.Empty;
        Assert.IsFalse(instructions.Contains("Track multi-step work with the todo tools"));
        StringAssert.Contains(instructions, "Always cite the runbook.");
    }

    // ---------------------------------------------------------------------------------------------
    // Loop + todos + delegation
    // ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TodosAndDelegationsRunToCompletion()
    {
        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Pull the customer list"}]}"""),
            FakeChatClient.ToolCall("c2", "background_agents_start_task", """{"agentName":"crm","input":"List active customers.","description":"Pull customers"}"""),
            FakeChatClient.Text("Delegated the fetch."),
            FakeChatClient.ToolCall("c3", "todos_complete", """{"items":[{"id":1,"reason":"Customer list returned"}]}"""),
            FakeChatClient.Text("There are 3 active customers."));

        var (agent, host, _) = CreateAgent(chatClient, args: new Dictionary<string, string>
        {
            [HarnessArgs.BackgroundAgents] = "owner1:crm"
        });

        host.Responders["owner1:crm"] = _ => "Found 3 customers.";

        await agent.OnInitialize();
        var response = await agent.OnMessage(Ask("How many active customers do we have?"));

        Assert.AreEqual("There are 3 active customers.", response.Message);

        // The loop terminated on its own, with every todo closed.
        Assert.AreEqual(0, (await agent.Harness!.GetRemainingTodosAsync()).Count);

        // The delegation actually reached the target grain as a real AgentMessage.
        Assert.AreEqual(1, host.ReceivedRequests.Count);
        Assert.AreEqual("owner1:crm", host.ReceivedRequests[0].ToHandle);
        Assert.AreEqual(CoordinatorHandle, host.ReceivedRequests[0].FromHandle);
    }

    [TestMethod]
    public async Task UnfinishedTodosSurviveTheIterationBudgetInsteadOfBeingDropped()
    {
        // The model adds a todo and never completes it. The loop must stop at the cap and the item must
        // still be readable, so the caller can report it rather than implying success.
        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Never finished"}]}"""),
            FakeChatClient.Text("Working on it."));

        var (agent, _, _) = CreateAgent(chatClient, args: new Dictionary<string, string>
        {
            [HarnessArgs.LoopMaxIterations] = "2"
        });

        await agent.OnInitialize();
        await agent.OnMessage(Ask("Do the thing"));

        var remaining = await agent.Harness!.GetRemainingTodosAsync();
        Assert.AreEqual(1, remaining.Count);
        Assert.AreEqual("Never finished", remaining[0].Title);

        // The loop really did re-invoke and then stop at the cap: the tool call and the text response are
        // one iteration, so a second model call can only come from the evaluator asking for another pass.
        Assert.IsTrue(
            chatClient.CallCount > 2,
            $"Expected the loop to re-invoke; the model was called {chatClient.CallCount} time(s).");
    }

    // ---------------------------------------------------------------------------------------------
    // Durability
    // ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TodosSurviveAcrossAgentInstances()
    {
        var host = new FakeAgentHost(CoordinatorHandle);

        var firstTurn = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Step one"},{"title":"Step two"},{"title":"Step three"}]}"""),
            FakeChatClient.ToolCall("c2", "todos_complete", """{"items":[{"id":1,"reason":"done"}]}"""),
            FakeChatClient.Text("One down."));

        var (first, _, _) = CreateAgent(firstTurn, host: host, args: new Dictionary<string, string>
        {
            [HarnessArgs.LoopMaxIterations] = "1"
        });

        await first.OnInitialize();
        await first.OnMessage(Ask("Work the plan"));

        Assert.AreEqual(2, (await first.Harness!.GetRemainingTodosAsync()).Count);
        Assert.IsTrue(host.CustomState.ContainsKey(SessionKey), "The turn should have persisted a session snapshot.");

        // A fresh proxy on the same host is what a grain reactivation looks like.
        var (second, _, _) = CreateAgent(FakeChatClient.WithTextResponse("Resuming."), host: host);
        await second.OnInitialize();

        Assert.IsTrue(second.Harness!.SessionRestored);
        var remaining = await second.Harness.GetRemainingTodosAsync();
        Assert.AreEqual(2, remaining.Count);
        CollectionAssert.AreEquivalent(
            new[] { "Step two", "Step three" },
            remaining.Select(todo => todo.Title).ToArray());
    }

    [TestMethod]
    public async Task NothingIsPersistedWhenSessionPersistenceIsOff()
    {
        var host = new FakeAgentHost(CoordinatorHandle);

        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Step one"}]}"""),
            FakeChatClient.Text("Started."));

        var (agent, _, _) = CreateAgent(chatClient, host: host, args: new Dictionary<string, string>
        {
            [HarnessArgs.SessionPersistence] = "false",
            [HarnessArgs.LoopMaxIterations] = "1"
        });

        await agent.OnInitialize();
        await agent.OnMessage(Ask("Work the plan"));

        Assert.IsFalse(agent.Harness!.IsSessionPersistent);
        Assert.IsFalse(host.CustomState.ContainsKey(SessionKey));
    }

    [TestMethod]
    public async Task AnUnreadableSnapshotIsArchivedAndTheRunStillSucceeds()
    {
        var host = new FakeAgentHost(CoordinatorHandle);
        using var document = JsonDocument.Parse("""{"Version":"not-a-number","ThreadId":"main"}""");
        host.CustomState[SessionKey] = document.RootElement.Clone();

        var (agent, _, _) = CreateAgent(FakeChatClient.WithTextResponse("Fresh start."), host: host);

        await agent.OnInitialize();
        var response = await agent.OnMessage(Ask("Do the thing"));

        Assert.AreEqual("Fresh start.", response.Message);
        Assert.IsFalse(agent.Harness!.SessionRestored);

        // Archived rather than deleted: the state resetting is survivable, destroying the evidence is not.
        Assert.IsTrue(host.CustomState.ContainsKey(CorruptKey));
        Assert.AreEqual(
            "not-a-number",
            host.CustomState[CorruptKey].GetProperty("Version").GetString());
    }

    [TestMethod]
    public async Task ASnapshotFromAnotherEnvelopeVersionStartsFresh()
    {
        var host = new FakeAgentHost(CoordinatorHandle);
        using var document = JsonDocument.Parse("""{"Version":99,"ThreadId":"main","Payload":{"stateBag":{}}}""");
        host.CustomState[SessionKey] = document.RootElement.Clone();

        var (agent, _, _) = CreateAgent(FakeChatClient.WithTextResponse("Fresh start."), host: host);

        await agent.OnInitialize();

        Assert.IsFalse(agent.Harness!.SessionRestored);
        Assert.IsFalse(host.CustomState.ContainsKey(CorruptKey), "A version mismatch is expected drift, not corruption.");
    }

    [TestMethod]
    public async Task DelegationsInFlightAtSnapshotTimeAreReportedAsLost()
    {
        // BackgroundAgentRuntimeState holds live Task objects behind [JsonIgnore], so a snapshot round-trip
        // strands anything mid-flight. Those must be surfaced, not silently forgotten.
        var host = new FakeAgentHost(CoordinatorHandle);
        using var document = JsonDocument.Parse(
            """
            {
              "Version": 1,
              "ThreadId": "main",
              "SavedUtc": "2026-08-03T00:00:00+00:00",
              "Payload": {
                "conversationId": null,
                "stateBag": {
                  "BackgroundAgentsProvider": {
                    "nextTaskId": 3,
                    "tasks": [
                      { "id": 1, "agentName": "crm", "description": "Pull customers", "status": 0 },
                      { "id": 2, "agentName": "crm", "description": "Pull orders", "status": 1 }
                    ]
                  }
                }
              }
            }
            """);
        host.CustomState[SessionKey] = document.RootElement.Clone();

        var (agent, _, _) = CreateAgent(FakeChatClient.WithTextResponse("Resuming."), host: host);

        await agent.OnInitialize();

        Assert.IsTrue(agent.Harness!.SessionRestored);
        Assert.AreEqual(1, agent.Harness.DelegationsLostOnRestore);
        StringAssert.Contains(agent.Harness.DescribeLostDelegations()!, "could not be recovered");
    }

    [TestMethod]
    public async Task ClearingTheHarnessSessionDropsBothTheSnapshotAndTheTodos()
    {
        var host = new FakeAgentHost(CoordinatorHandle);

        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Step one"}]}"""),
            FakeChatClient.Text("Started."));

        var (agent, _, _) = CreateAgent(chatClient, host: host, args: new Dictionary<string, string>
        {
            [HarnessArgs.LoopMaxIterations] = "1"
        });

        await agent.OnInitialize();
        await agent.OnMessage(Ask("Work the plan"));
        Assert.AreEqual(1, (await agent.Harness!.GetRemainingTodosAsync()).Count);

        await agent.Harness.ClearHarnessSessionAsync();

        Assert.IsFalse(host.CustomState.ContainsKey(SessionKey));
        Assert.AreEqual(0, (await agent.Harness.GetRemainingTodosAsync()).Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Roster and delegation
    // ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheRosterExcludesUnreachableAndUnconfiguredAgentsWithAReason()
    {
        var host = new FakeAgentHost(CoordinatorHandle);
        host.HealthResponders["owner1:down"] = () => throw new InvalidOperationException("Silo unreachable.");
        host.HealthResponders["owner1:blank"] = () => new AgentHealthStatus
        {
            Handle = "owner1:blank",
            State = HealthState.Unhealthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = false
        };

        var roster = await AgentRosterBuilder.BuildAsync(
            ["owner1:crm", "owner1:down", "owner1:blank"],
            host);

        Assert.AreEqual(1, roster.Available.Count);
        Assert.AreEqual("crm", roster.Available[0].Name);
        Assert.AreEqual(2, roster.Unavailable.Count);
        StringAssert.Contains(roster.DescribeUnavailable(), "Silo unreachable.");
        StringAssert.Contains(roster.DescribeUnavailable(), "Agent is not configured.");
    }

    [TestMethod]
    public async Task TheRosterDeduplicatesNamesThatCollideAcrossPrincipals()
    {
        // BackgroundAgentsProvider throws on duplicate names (case-insensitively), so the collision has to
        // be resolved here, before construction.
        var host = new FakeAgentHost(CoordinatorHandle);

        var roster = await AgentRosterBuilder.BuildAsync(
            ["owner1:crm", "owner2:CRM", "owner3:crm"],
            host);

        CollectionAssert.AreEqual(
            new[] { "crm", "CRM-2", "crm-3" },
            roster.Available.Select(entry => entry.Name).ToArray());

        // Proof that the resolved names satisfy the provider's own validation.
        var delegates = FabrCoreBackgroundAgent.FromRoster(roster, host);
        _ = new BackgroundAgentsProvider(delegates);
    }

    [TestMethod]
    public async Task TheRosterUsesTheTargetAgentDescription()
    {
        var host = new FakeAgentHost(CoordinatorHandle);
        host.HealthResponders["owner1:crm"] = () => new AgentHealthStatus
        {
            Handle = "owner1:crm",
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
            Configuration = new AgentConfiguration
            {
                Handle = "owner1:crm",
                Description = "Answers questions about customer records."
            }
        };

        var roster = await AgentRosterBuilder.BuildAsync(["owner1:crm"], host);

        Assert.AreEqual("Answers questions about customer records.", roster.Available[0].Description);
    }

    [TestMethod]
    public async Task ADelegationThatOverrunsItsTimeoutFails()
    {
        // The host send has no cancellation surface, so the agent must bound it itself — otherwise a wedged
        // target hangs the delegating agent's whole turn.
        var host = new FakeAgentHost(CoordinatorHandle);
        host.Delays["owner1:slow"] = TimeSpan.FromSeconds(30);

        var delegateAgent = new FabrCoreBackgroundAgent(
            host,
            "owner1:slow",
            "slow",
            "A slow agent",
            TimeSpan.FromMilliseconds(50));

        try
        {
            await delegateAgent.RunAsync("Do the thing");
            Assert.Fail("Expected the delegation to time out.");
        }
        catch (TimeoutException)
        {
        }
    }

    [TestMethod]
    public void ABackgroundAgentWithoutANameIsRejectedAtConstruction()
    {
        var host = new FakeAgentHost(CoordinatorHandle);

        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new FabrCoreBackgroundAgent(host, "owner1:crm", "  ", "A description"));
    }

    // ---------------------------------------------------------------------------------------------
    // Argument parsing
    // ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TodosCanBeSwitchedOffByArg()
    {
        var (agent, _, _) = CreateAgent(args: new Dictionary<string, string>
        {
            [HarnessArgs.Todo] = "false"
        });

        await agent.OnInitialize();

        Assert.IsNull(agent.Harness!.Todos);
        Assert.IsFalse(agent.Harness.Agent.IsLooping, "With todos off and no delegates, nothing can drive a loop.");
    }

    [TestMethod]
    public async Task AnUnparseableArgLeavesTheDefaultInPlace()
    {
        // The established convention for underscore args: bad values fall back silently rather than
        // failing the agent.
        var (agent, _, _) = CreateAgent(args: new Dictionary<string, string>
        {
            [HarnessArgs.Todo] = "maybe",
            [HarnessArgs.LoopMaxIterations] = "lots"
        });

        await agent.OnInitialize();

        Assert.IsNotNull(agent.Harness!.Todos);
        Assert.IsTrue(agent.Harness.Agent.IsLooping);
    }

    [TestMethod]
    public async Task AnUnrecognizedLoopTokenIsIgnoredRatherThanThrowing()
    {
        var (agent, _, _) = CreateAgent(args: new Dictionary<string, string>
        {
            [HarnessArgs.Loop] = "todo,teleport"
        });

        await agent.OnInitialize();

        Assert.IsTrue(agent.Harness!.Agent.IsLooping);
    }

    [TestMethod]
    public async Task ABackgroundLoopWithNoReachableDelegatesIsRejectedLoudly()
    {
        // Asking for a delegation loop with nothing to delegate to is a misconfiguration, not something to
        // paper over — the loop would have no way to observe progress.
        var host = new FakeAgentHost(CoordinatorHandle);
        host.HealthResponders["owner1:down"] = () => throw new InvalidOperationException("Silo unreachable.");

        var (agent, _, _) = CreateAgent(host: host, args: new Dictionary<string, string>
        {
            [HarnessArgs.Loop] = "background",
            [HarnessArgs.BackgroundAgents] = "owner1:down"
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(agent.OnInitialize);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static AgentMessage Ask(string message) => new()
    {
        FromHandle = "owner1",
        ToHandle = CoordinatorHandle,
        Kind = MessageKind.Request,
        Message = message
    };

    private static (HarnessTestAgent Agent, FakeAgentHost Host, FakeChatClient ChatClient) CreateAgent(
        FakeChatClient? chatClient = null,
        Dictionary<string, string>? args = null,
        FakeAgentHost? host = null,
        string systemPrompt = "You are a test agent.")
    {
        chatClient ??= FakeChatClient.WithTextResponse("Done.");
        host ??= new FakeAgentHost(CoordinatorHandle);

        var config = new AgentConfiguration
        {
            Handle = CoordinatorHandle,
            AgentType = "harness-test",
            Models = "default",
            SystemPrompt = systemPrompt,
            Args = args ?? []
        };

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IFabrCoreChatClientService>(new FakeChatClientService(chatClient))
            .BuildServiceProvider();

        return (new HarnessTestAgent(config, services, host), host, chatClient);
    }

    private sealed class HarnessTestAgent : FabrCoreAgentProxy
    {
        public HarnessTestAgent(AgentConfiguration config, IServiceProvider serviceProvider, IFabrCoreAgentHost host)
            : base(config, serviceProvider, host)
        {
        }

        public FabrCoreHarnessResult? Harness { get; private set; }

        public override async Task OnInitialize()
            => Harness = await CreateFabrCoreHarnessAgent(config.Models ?? "default", "main");

        public override async Task<AgentMessage> OnMessage(AgentMessage message)
        {
            var run = await Harness!.RunAsync(message.Message ?? string.Empty);
            var response = message.Response();
            response.Message = run.Text;
            return response;
        }
    }
}
