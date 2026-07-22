// CLIENT AGENT TEMPLATE FOR SWARM PARTICIPATION
//
// You build this. The swarm does NOT build client agents. Your application
// owns the agent — its prompts, plugins, tools, model, and business logic.
//
// The swarm provisions a long-lived WORKER agent (with its own LLM) that is
// paired 1:1 with your client agent. The worker reasons about tasks and
// delegates domain work to your agent via its DelegateToClientAgent tool.
// Your agent receives a plain AgentMessage and replies normally.
//
// Two things to do besides writing this file:
//
//   1. List the agent alias in fabrcore-swarm.json under the named
//      SwarmDefinition's AgentHandles array:
//        {
//          "SwarmDefinitions": [{
//            "Name": "my-workflow",
//            "AgentHandles": ["my-domain-agent"]
//          }]
//        }
//
//   2. Bring the agent online at startup:
//        await agentService.ConfigureAgentAsync("system", new AgentConfiguration
//        {
//            Handle = "my-domain-agent",
//            AgentType = "my-domain-agent",
//            Models = "default",
//            Plugins = ["my-plugin"],
//            Tools = ["my-tool"],
//            SystemPrompt = "You are a domain expert..."
//        });
//
// The swarm's LiveAgentRegistry picks up the class-level [Description],
// [FabrCoreCapabilities], and [FabrCoreNote] attributes below via
// IFabrCoreRegistry, and the runtime plugins/tools/prompt via GetAgentHealth.
// Both feed the planner's "what can this agent do" view automatically.

using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Experimental.Swarm;
using FabrCore.Experimental.Swarm.Configuration;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyApp.Agents;

[AgentAlias("my-domain-agent")]
[Description("Domain expert that does the thing for the swarm")]
[FabrCoreCapabilities("Reads, transforms, and writes domain data via the my-plugin plugin.")]
[FabrCoreNote("Requires a job number or entity ID in the task description.")]
public class MyDomainAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public MyDomainAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // Build the agent normally — your plugins, your model, your prompt.
        // The swarm does not configure any of this; it's your domain agent.
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);

        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        if (_agent is null || _session is null)
        {
            var err = message.Response();
            err.Message = "Agent is not initialized";
            return err;
        }

        var response = message.Response();

        // The swarm sends task assignments as MessageType = swarm-task-assignment.
        // You can also handle other message types (chat, queries, whatever you need).
        if (message.MessageType == SwarmMessageTypes.TaskAssignment)
        {
            return await HandleSwarmTask(message);
        }

        // Default: just run the LLM on the message text. Useful for direct
        // chat with this agent outside of swarm dispatches.
        var defaultResult = await _agent.RunAsync(
            new ChatMessage(ChatRole.User, message.Message ?? ""), _session);
        response.Message = defaultResult.Messages.Last().Text ?? "";
        return response;
    }

    private async Task<AgentMessage> HandleSwarmTask(AgentMessage message)
    {
        var response = message.Response();
        var state = message.State ?? new Dictionary<string, string>();

        // The adapter worker passes these state keys (all optional except
        // taskId/planId, which are informational):
        //   taskId            — task identifier
        //   planId            — plan identifier
        //   inputContext      — formatted results from completed dependency tasks
        //   blackboardHandle  — for direct blackboard access if you want it
        //   clientAgentGuidance — optional extra prompt guidance from the swarm definition
        var taskDescription = message.Message ?? "";
        var inputContext = state.GetValueOrDefault("inputContext", "");
        var blackboardHandle = state.GetValueOrDefault("blackboardHandle", "");
        var extraGuidance = state.GetValueOrDefault("clientAgentGuidance", "");

        try
        {
            // Build the prompt. SwarmPromptFoundation.ClientAgentGuidance is
            // OPTIONAL — include it if you want your LLM to follow swarm reply
            // conventions automatically (write large data to blackboard, prefer
            // roadblock over fake completion, etc).
            var prompt = $"""
                {SwarmPromptFoundation.ClientAgentGuidance}

                {extraGuidance}

                Task: {taskDescription}
                {(string.IsNullOrEmpty(inputContext) ? "" : $"\nContext from prior tasks:\n{inputContext}")}
                """;

            // Run the LLM on a per-task session so the conversation is isolated.
            var taskSession = await _agent!.CreateSessionAsync();
            var result = await _agent.RunAsync(new ChatMessage(ChatRole.User, prompt), taskSession);
            var taskResult = result.Messages.Last().Text ?? "";

            // SUCCESS REPLY (the default convention):
            // Just return the result. No special state needed.
            response.Message = taskResult;
            return response;

            // ROADBLOCK REPLY (when you cannot complete the task):
            // Replace the lines above with:
            //   response.Message = "Reason this task cannot be completed";
            //   response.State ??= new Dictionary<string, string>();
            //   response.State["swarm-task-status"] = "roadblock";
            //   response.State["swarm-roadblock-question"] = "Specific question for the human";
            //   response.State["swarm-roadblock-type"] = "NeedsInput";
            //       // or "MissingData" / "ToolFailure" / "CapabilityGap" / "Other"
            //   return response;
        }
        catch (Exception ex)
        {
            // Treat exceptions as roadblocks so the orchestrator escalates instead
            // of marking the task complete with a garbage result.
            response.Message = $"Failed to execute task: {ex.Message}";
            response.State ??= new Dictionary<string, string>();
            response.State["swarm-task-status"] = "roadblock";
            response.State["swarm-roadblock-type"] = "ToolFailure";
            return response;
        }
    }

    public override Task OnEvent(EventMessage eventMessage)
    {
        // Optional: subscribe to events. Most client agents don't need this.
        return Task.CompletedTask;
    }

    // OPTIONAL: helpers to read or write the per-plan blackboard if you want
    // to coordinate large shared data with sibling agents in the same plan.
    private async Task<string> ReadFromBlackboard(string blackboardHandle, string key)
    {
        var reply = await fabrcoreAgentHost.SendAndReceiveMessage(new AgentMessage
        {
            ToHandle = blackboardHandle,
            MessageType = SwarmMessageTypes.BlackboardRead,
            Kind = MessageKind.Request,
            Message = $"Read {key}",
            State = new Dictionary<string, string> { ["key"] = key }
        });
        return reply.Message ?? "";
    }

    private async Task WriteToBlackboard(string blackboardHandle, string key, string value)
    {
        await fabrcoreAgentHost.SendAndReceiveMessage(new AgentMessage
        {
            ToHandle = blackboardHandle,
            MessageType = SwarmMessageTypes.BlackboardWrite,
            Kind = MessageKind.Request,
            Message = $"Write {key}",
            State = new Dictionary<string, string>
            {
                ["key"] = key,
                ["value"] = value
            }
        });
    }
}
