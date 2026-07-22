// SME (Subject Matter Expert) agent template.
//
// SMEs answer "how do I…", "what does the user mean…", roadblock-help, and
// planning-context questions from the TaskAgent. They are NOT task executors —
// the planner cannot assign tasks to SMEs.
//
// Wire format: MessageType = "swarm-sme-consultation" (kept identical to the
// Swarm convention so SMEs can serve both TaskAgents and Swarms unchanged).
//
// Reply convention:
//   - Set message.State["sme-status"] = "answered" if you have an answer
//   - Set message.State["sme-status"] = "unknown" if you can't help
//   - A non-empty Message with no explicit "unknown" status is also accepted
//     as an answer (defensive — older SME agents that forget to set the flag
//     still work).
//
// The TaskAgent times out per-SME at 30 seconds. Multiple SMEs are queried
// in parallel via SmeConsultationService.ConsultAsync — first answer wins.

using System.ComponentModel;
using FabrCore.Agents.TaskAgent;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyApp.Agents;

[AgentAlias("job-policy-sme")]
[Description("Subject-matter expert on job-shop manufacturing policy and best practices.")]
[FabrCoreCapabilities(
    "Answers questions about job status workflows, allocation rules, " +
    "compliance requirements, and standard procedures. Pulls from the policy " +
    "knowledge base. Decisive and terse.")]
[FabrCoreNote("Use for 'how should we…' and 'what's the standard for…' questions, not for executing actions.")]
public class JobPolicySmeAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public JobPolicySmeAgent(
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
        response.State ??= new Dictionary<string, string>();

        // Only handle consultation messages here; let other message types
        // fall through to a generic handler if you need that.
        if (message.MessageType != TaskAgentMessageTypes.SmeConsultation)
        {
            response.Message = "I'm a policy SME — send me consultation messages.";
            return response;
        }

        var question = message.Message ?? "";
        var context = message.State?.GetValueOrDefault("context");

        var prompt =
            "You are a subject-matter expert. Answer the question directly using your tools and knowledge base.\n" +
            "If you don't know, say so explicitly — never guess.\n" +
            "Keep answers terse: a short paragraph or a bulleted list.\n" +
            "End your reply with a fenced fabrcore-envelope block:\n\n" +
            "```fabrcore-envelope\n" +
            "{\n" +
            "  \"status\": \"completed|info\",\n" +
            "  \"summary\": \"<one-line answer>\",\n" +
            "  \"confidence\": 0.0\n" +
            "}\n" +
            "```\n\n" +
            (string.IsNullOrEmpty(context) ? "" : $"Context: {context}\n\n") +
            $"Question: {question}";

        try
        {
            var result = await _agent!.RunAsync(
                new ChatMessage(ChatRole.User, prompt), _session!);
            var answer = result.Messages.LastOrDefault()?.Text ?? "";

            if (LooksLikeUnknown(answer))
            {
                response.State["sme-status"] = "unknown";
                response.Message = "";
            }
            else
            {
                response.State["sme-status"] = "answered";
                response.Message = answer;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SME consultation failed");
            response.State["sme-status"] = "unknown";
            response.Message = "";
        }

        return response;
    }

    private static bool LooksLikeUnknown(string answer)
    {
        var lower = answer.ToLowerInvariant();
        return lower.Contains("i don't know")
            || lower.Contains("i do not know")
            || lower.Contains("cannot answer")
            || lower.Contains("not enough information");
    }

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;
}
