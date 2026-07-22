// CLIENT AGENT TEST TEMPLATE
//
// You don't test the swarm worker — it's an adapter and has no LLM. You test
// your CLIENT agents to make sure they handle swarm task assignments correctly
// and produce the right replies (success vs roadblock).
//
// This template shows two patterns:
//   1. Test that your client agent completes a task and replies normally
//   2. Test that your client agent signals a roadblock when it should
//
// Plus a "fake client agent" example you can use as a stand-in when integration-
// testing the orchestrator + planner + supervisor end-to-end.

using FabrCore.Core;
using FabrCore.Experimental.Swarm;
using FabrCore.Experimental.Swarm.Models;
using FabrCore.Sdk;
using FabrCore.Tests.Infrastructure;

namespace MyApp.Agents.Tests;

[TestClass]
public class MyDomainAgentTests
{
    [TestMethod]
    public async Task ClientAgent_HandlesSwarmTaskAssignment_RepliesWithCompletion()
    {
        using var harness = new FabrCoreTestHarness();

        var chatClient = FakeChatClient.WithSequentialResponses(
            "Allocated plate 42790 to pipe section A");

        var config = new AgentConfiguration
        {
            Handle = "test:my-domain-agent",
            AgentType = "my-domain-agent",
            Models = "default",
            Plugins = ["my-plugin"]  // your real plugin set
        };

        var agent = harness.CreateMockAgent<MyDomainAgent>(chatClient, config);
        await harness.InitializeAgent(agent);

        // The adapter worker would normally send this. Test it directly.
        var taskAssignment = new AgentMessage
        {
            FromHandle = "test:swarm-worker-task-1",
            MessageType = SwarmMessageTypes.TaskAssignment,
            Message = "Allocate a plate to pipe section A",
            State = new Dictionary<string, string>
            {
                ["taskId"] = "test-task-1",
                ["planId"] = "test-plan",
                ["inputContext"] = "Pipe section A is 56 inches OD",
                ["blackboardHandle"] = "test:swarm-blackboard"
            }
        };

        var response = await harness.SendMessage(agent, taskAssignment);

        // Successful completion: response.Message has the result, and there's
        // NO swarm-task-status state key (or it's set to "complete").
        Assert.IsNotNull(response.Message);
        Assert.IsTrue(response.Message!.Contains("Allocated"));

        var status = response.State?.GetValueOrDefault("swarm-task-status", "complete");
        Assert.AreEqual("complete", status);
    }

    [TestMethod]
    public async Task ClientAgent_MissingRequiredContext_RepliesWithRoadblock()
    {
        using var harness = new FabrCoreTestHarness();

        // Simulate the agent's LLM detecting missing context. Your real agent's
        // logic would notice the absence of a required field in the task
        // description and signal a roadblock.
        var chatClient = FakeChatClient.WithSequentialResponses(
            "Cannot allocate — no pipe ID specified in the task");

        var config = new AgentConfiguration
        {
            Handle = "test:my-domain-agent-roadblock",
            AgentType = "my-domain-agent",
            Models = "default"
        };

        var agent = harness.CreateMockAgent<MyDomainAgent>(chatClient, config);
        await harness.InitializeAgent(agent);

        var taskAssignment = new AgentMessage
        {
            FromHandle = "test:swarm-worker-task-2",
            MessageType = SwarmMessageTypes.TaskAssignment,
            Message = "Allocate a plate to a pipe",   // intentionally vague
            State = new Dictionary<string, string>
            {
                ["taskId"] = "test-task-2",
                ["planId"] = "test-plan"
            }
        };

        var response = await harness.SendMessage(agent, taskAssignment);

        // Roadblock: swarm-task-status state key set to "roadblock"
        var status = response.State?.GetValueOrDefault("swarm-task-status");
        Assert.AreEqual("roadblock", status);
        Assert.IsNotNull(response.State?.GetValueOrDefault("swarm-roadblock-question"));
    }
}

/// <summary>
/// Minimal fake client agent for integration tests of the swarm orchestrator.
/// Echoes the task description back as the result. Use this when you want to
/// exercise the planner + supervisor + adapter chain without depending on a
/// real domain agent.
/// </summary>
[AgentAlias("fake-echo-agent")]
public class FakeEchoAgent : FabrCoreAgentProxy
{
    public FakeEchoAgent(
        AgentConfiguration c, IServiceProvider sp, IFabrCoreAgentHost h)
        : base(c, sp, h) { }

    public override Task OnInitialize() => Task.CompletedTask;

    public override Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        if (message.MessageType == SwarmMessageTypes.TaskAssignment)
        {
            // Successful completion path — just echo the task description
            response.Message = $"Echo: {message.Message}";
        }
        else
        {
            response.Message = "Fake echo agent online";
        }

        return Task.FromResult(response);
    }

    public override Task OnEvent(EventMessage e) => Task.CompletedTask;
}

/// <summary>
/// Fake client agent that always reports a roadblock. Useful for testing that
/// the orchestrator correctly escalates roadblocks to the human.
/// </summary>
[AgentAlias("fake-stuck-agent")]
public class FakeStuckAgent : FabrCoreAgentProxy
{
    public FakeStuckAgent(
        AgentConfiguration c, IServiceProvider sp, IFabrCoreAgentHost h)
        : base(c, sp, h) { }

    public override Task OnInitialize() => Task.CompletedTask;

    public override Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        response.Message = $"Cannot complete: {message.Message}";
        response.State = new Dictionary<string, string>
        {
            ["swarm-task-status"] = "roadblock",
            ["swarm-roadblock-question"] = "What should I do?",
            ["swarm-roadblock-type"] = "NeedsInput"
        };
        return Task.FromResult(response);
    }

    public override Task OnEvent(EventMessage e) => Task.CompletedTask;
}
