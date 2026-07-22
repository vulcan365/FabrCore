// Client agent template — what your domain agents look like when they
// participate in a TaskAgent's plan.
//
// The TaskAgent dispatches via SendAndReceiveMessage with
// MessageType = "task-delegation". The message.Message is a structured prompt
// with three sections:
//   - "Upstream context" — results from prior tasks + active rule references
//   - "Task" — the imperative description from the plan
//   - A trailing instruction asking for a fabrcore-envelope JSON tail
//
// State (Dictionary<string,string>) carries the same context as named keys
// (original_goal, plan_summary, prior_results, sme_note, etc.) for agents
// that prefer structured access.
//
// The agent should respond with prose followed by a fenced fabrcore-envelope
// block so the TaskAgent's DelegationService can read status/data/confidence
// without parsing prose.

using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyApp.Agents;

[AgentAlias("job-execution-agent")]
[Description("Executes manufacturing job operations — create, update, transition status, allocate resources.")]
[FabrCoreCapabilities(
    "Handles job-shop manufacturing job CRUD and status workflow. Can create jobs, " +
    "update fields, allocate plates and pipes, and transition through Planning → " +
    "Released → InProgress → Complete states. Pulls live data via the job plugin.")]
[FabrCoreNote("Requires a job number in context before most operations can run.")]
[FabrCoreNote("Do not use this agent for quoting or estimating — use quote-agent instead.")]
public class JobExecutionAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public JobExecutionAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
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
        var response = message.Response();

        // Detect TaskAgent delegations vs. direct user messages so you can
        // tailor the system prompt or response shape if you want.
        var isDelegation = message.MessageType == "task-delegation";

        // The TaskAgent's prompt already includes context + task + envelope
        // instruction. Just hand it to the model.
        var prompt = message.Message ?? "";

        // Optional: pull structured context from State for agents that want it.
        var originalGoal = message.State?.GetValueOrDefault("original_goal");
        var smeNote = message.State?.GetValueOrDefault("sme_note");
        // Use these for logging, validation, or to enrich your system prompt.

        try
        {
            var result = await _agent!.RunAsync(
                new ChatMessage(ChatRole.User, prompt), _session!);
            response.Message = result.Messages.LastOrDefault()?.Text ?? "";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job execution agent failed on delegation");
            // Emit a failed envelope so the TaskAgent triggers SME consultation
            // and replan rather than stalling.
            response.Message =
                "I hit an unexpected error while running this task.\n\n" +
                "```fabrcore-envelope\n" +
                "{\n" +
                "  \"status\": \"failed\",\n" +
                $"  \"summary\": \"{ex.GetType().Name}: {ex.Message.Replace("\"", "'")}\",\n" +
                "  \"warnings\": [\"check upstream context — input may have been incomplete\"]\n" +
                "}\n" +
                "```";
        }

        return response;
    }

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;
}

// ─────────────────────────────────────────────────────────────────────────────
// Sample envelope your client agent should append to its prose response.
// The TaskAgent's DelegationService reads `status` to decide completed/failed/
// partial without parsing prose, then strips the block before storing the
// result on the TaskItem.
//
//   ```fabrcore-envelope
//   {
//     "status": "completed",
//     "summary": "Created job 4 with 14 pipes",
//     "data": {
//       "job_id": "4",
//       "pipe_count": 14,
//       "next_status": "Planning"
//     },
//     "confidence": 0.95,
//     "warnings": []
//   }
//   ```
//
// Status semantics:
//   completed — task done, replanner invoked next
//   failed    — TaskAgent consults SME, retries up to MaxAttempts, then
//               triggers ReplanTrigger.TaskFailed
//   partial   — treated as success this attempt; replanner may extend the plan
//   info      — treated as success
//
// A missing or malformed envelope falls back to prose-only success — the
// TaskAgent never breaks on it. Always emit the envelope when you can.
// ─────────────────────────────────────────────────────────────────────────────
