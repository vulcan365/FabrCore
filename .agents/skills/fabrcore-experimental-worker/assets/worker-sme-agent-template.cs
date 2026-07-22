// Template: an SME agent that responds to Worker / TaskAgent SME consultation
// messages. The wire-level MessageType is "swarm-sme-consultation" — same
// across Worker and TaskAgent, so this template works for both.
//
// Replace {{SME_ALIAS}} / {{SME_NAME}} / {{SME_DESCRIPTION}} / {{SME_CAPABILITIES}}.
//
// Note: the [Description] and [FabrCoreCapabilities] attributes are surfaced
// via IFabrCoreRegistry and are auto-pulled by Worker as fallback metadata
// for the SmeRouter when the host's fabrcore-worker.json doesn't provide
// Description / GoodFor on the SmeReference entry.

using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MyApp.Agents;

/// <summary>
/// {{SME_DESCRIPTION}}
/// SME agent that answers Worker / TaskAgent consultation requests.
/// </summary>
[AgentAlias("{{SME_ALIAS}}")]
[Description("{{SME_DESCRIPTION}}")]
[FabrCoreCapabilities("{{SME_CAPABILITIES}}")]
public class {{SME_NAME}} : FabrCoreAgentProxy
{
    private const string SmeConsultationMessageType = "swarm-sme-consultation";

    private AIAgent? _agent;
    private AgentSession? _session;

    public {{SME_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: $"{config.Handle}:sme",
            tools: null);   // SMEs usually answer from training + memory, no tools required
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        // Worker / TaskAgent send a request with MessageType = "swarm-sme-consultation".
        // The question is in Message; optional prior context is in State["context"].
        if (message.MessageType == SmeConsultationMessageType)
        {
            return await AnswerConsultationAsync(message);
        }

        // Direct user message — fall back to a general answer.
        return await AnswerGeneralAsync(message);
    }

    private async Task<AgentMessage> AnswerConsultationAsync(AgentMessage message)
    {
        var question = message.Message ?? "";
        var context = message.State?.GetValueOrDefault("context");

        var systemPrompt = """
            You are a subject-matter expert agent. A worker / task agent has
            asked you for guidance. Be concise and specific.

            If you can help: respond with the guidance directly. Two short
            paragraphs maximum.

            If you cannot help (the question is outside your domain): respond
            with "I can't help with this — outside my domain." and nothing
            more. Worker will detect the sme-status=unknown signal and skip you.
            """;

        var prompt = string.IsNullOrEmpty(context)
            ? question
            : $"Question:\n{question}\n\nPrior context (response that failed validation, etc.):\n{context}";

        var systemMsg = new ChatMessage(ChatRole.System, systemPrompt);
        var userMsg = new ChatMessage(ChatRole.User, prompt);

        // Fresh session — consultation requests are stateless.
        var session = await _agent!.CreateSessionAsync();
        var result = await _agent.RunAsync(new[] { systemMsg, userMsg }, session);
        var text = result.Messages.LastOrDefault()?.Text ?? "";

        // Signal the consultation outcome via State["sme-status"].
        var response = message.Response();
        response.State ??= new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("outside my domain", StringComparison.OrdinalIgnoreCase))
        {
            response.State["sme-status"] = "unknown";
            response.Message = "";
        }
        else
        {
            response.State["sme-status"] = "answered";
            response.Message = text;
        }

        return response;
    }

    private async Task<AgentMessage> AnswerGeneralAsync(AgentMessage message)
    {
        var chat = new ChatMessage(ChatRole.User, message.Message ?? "");
        var sb = new System.Text.StringBuilder();
        await foreach (var update in _agent!.RunStreamingAsync(chat, _session!))
            sb.Append(update.Text);
        return message.Response(sb.ToString());
    }
}
