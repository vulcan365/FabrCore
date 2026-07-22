// Template: host agent that uses Worker with EXPLICIT stage calls.
// Use this template when you want to:
//   - Inspect pre.PromptOverlay before deciding to use it,
//   - Stream tokens back to the user mid-turn,
//   - Run different prompts on first pass vs retry,
//   - Skip validation under certain conditions.
//
// If your flow is "one LLM call, one validation, one optional retry," use
// the ProcessAsync template instead.

using System.ComponentModel;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Experimental.Worker.Abstractions;
using FabrCore.Experimental.Worker.Model;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Agents;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// Host agent using FabrCore.Experimental.Worker with explicit stage calls.
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private IWorkerService? _worker;
    private AIAgent? _agent;
    private AgentSession? _session;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: $"{config.Handle}:main",
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;

        var capabilities = (tools ?? Enumerable.Empty<AITool>())
            .Select(WorkerCapability.FromAITool)
            .ToList();

        var provider = serviceProvider.GetRequiredService<IWorkerProvider>();
        _worker = await provider.GetWorkerServiceAsync(
            agentHost: fabrcoreAgentHost,
            agentHandle: fabrcoreAgentHost.GetHandle(),
            configName: config.Args?.GetValueOrDefault("WorkerDefinition"),
            analysisAgent: _agent!,
            capabilities: capabilities,
            agentFactory: async (modelName, ct) =>
                (await CreateChatClientAgent(
                    modelName, $"{config.Handle}:worker:{modelName}", tools: null)).Agent);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        // ===== Stage 1: PreProcess =====
        var pre = await _worker!.PreProcessAsync(message);

        // Inspect what Worker decided before injecting. For example, you might
        // decide to skip the overlay when every task is NotFeasible — and
        // instead respond with a clean "I can't help with this" directly.
        var allNotFeasible = pre.TaskList.Tasks.Count > 0
            && pre.TaskList.Tasks.All(t => t.Feasibility == WorkerTaskFeasibility.NotFeasible);
        if (allNotFeasible)
        {
            return message.Response(
                "I don't have the tools to do this. Here's what I'd need: "
                + string.Join("; ", pre.TaskList.Tasks
                    .Select(t => $"{t.Description} — {t.FeasibilityReason}")));
        }

        // ===== Run the main LLM =====
        var chat = new ChatMessage(ChatRole.User, message.Message + pre.PromptOverlay);
        var response = "";
        await foreach (var update in _agent!.RunStreamingAsync(chat, _session!))
            response += update.Text;
        response = StripWorkerEnvelopeBlocks(response);

        // ===== Stage 2: Validate =====
        var validation = await _worker.ValidateAsync(message, response);

        if (validation.IsSatisfied)
            return message.Response(response);

        // ===== Optional: explicit retry =====
        // RetryWithGuidanceAsync invokes your retryRunner with a stage context
        // that has RetryGuidance populated. Worker does NOT re-validate after
        // the retry — call ValidateAsync again if you care.
        response = await _worker.RetryWithGuidanceAsync(
            message, response, validation,
            retryRunner: async (ctx, ct) =>
            {
                var retryChat = new ChatMessage(
                    ChatRole.User,
                    message.Message + ctx.RetryGuidance);
                var sb = new System.Text.StringBuilder();
                await foreach (var update in _agent.RunStreamingAsync(
                    retryChat, _session, cancellationToken: ct))
                {
                    sb.Append(update.Text);
                }
                return sb.ToString();
            });
        response = StripWorkerEnvelopeBlocks(response);

        // Optional re-validation (skip if you trust the retry):
        var revalidation = await _worker.ValidateAsync(message, response);
        logger.LogInformation(
            "WORKER: retry satisfied={Sat} remainingGaps={Gaps}",
            revalidation.IsSatisfied, revalidation.Gaps.Count);

        return message.Response(response);
    }

    private Task<IEnumerable<AITool>> ResolveConfiguredToolsAsync() =>
        Task.FromResult<IEnumerable<AITool>>(Array.Empty<AITool>());

    private static string StripWorkerEnvelopeBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var cleaned = Regex.Replace(
            text,
            @"```fabrcore-worker-envelope\s*\r?\n.*?```",
            "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var openFence = cleaned.IndexOf("```fabrcore-worker-envelope", StringComparison.OrdinalIgnoreCase);
        if (openFence >= 0)
            cleaned = cleaned[..openFence];

        return cleaned.Trim();
    }
}
