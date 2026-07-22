// Template: host agent that uses Worker via the ProcessAsync convenience wrapper.
// Replace {{AGENT_ALIAS}} / {{AGENT_NAME}} / {{AGENT_DESCRIPTION}} placeholders.
//
// This template assumes:
//   - You already have AddFabrCoreServer() in Program.cs
//   - You called services.AddWorkerServices(...) (see server-registration.cs)
//   - fabrcore-worker.json sits in the working directory
//   - config.Args["WorkerDefinition"] = "<one of the names in the JSON>"

using System.ComponentModel;
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
/// Host agent using FabrCore.Experimental.Worker for task-tracking + validation.
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private IWorkerService? _worker;
    private AIAgent? _agent;
    private AgentSession? _session;
    private List<WorkerCapability>? _capabilities;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Build the main LLM agent + the host's main session.
        //    Add your tools here — these are also what Worker uses for feasibility judgments.
        var tools = await ResolveConfiguredToolsAsync();   // your existing tool resolution

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: $"{config.Handle}:main",
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;

        // 2. Map the tool inventory into capability entries. The extractor uses
        //    these to judge per-task feasibility.
        _capabilities = (tools ?? Enumerable.Empty<AITool>())
            .Select(WorkerCapability.FromAITool)
            .ToList();

        // 3. Resolve the worker service for this agent handle.
        var provider = serviceProvider.GetRequiredService<IWorkerProvider>();
        _worker = await provider.GetWorkerServiceAsync(
            agentHost: fabrcoreAgentHost,
            agentHandle: fabrcoreAgentHost.GetHandle(),
            configName: config.Args?.GetValueOrDefault("WorkerDefinition"),
            analysisAgent: _agent!,
            capabilities: _capabilities,
            // Required when any WorkerDefinition uses TaskExtractionModelName /
            // ValidationModelName / SmeRouterModelName / InternalAdvisorModelName.
            // The factory hands Worker a model-specific AIAgent on demand.
            agentFactory: async (modelName, ct) =>
                (await CreateChatClientAgent(
                    modelName,
                    threadId: $"{config.Handle}:worker:{modelName}",
                    tools: null)).Agent);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        // ProcessAsync runs PreProcess → agentRunner → Validate → optional retry → return.
        // The host's only responsibility is to run the main LLM inside agentRunner.
        var result = await _worker!.ProcessAsync(
            message,
            agentRunner: async (ctx, ct) =>
            {
                // On the first pass, append ctx.PromptOverlay (task list + intake advice).
                // On the retry pass, append ctx.RetryGuidance (gaps + gap advice).
                var overlay = ctx.RetryCount == 0 ? ctx.PromptOverlay : ctx.RetryGuidance;
                var chat = new ChatMessage(ChatRole.User, message.Message + overlay);

                var sb = new System.Text.StringBuilder();
                await foreach (var update in _agent!.RunStreamingAsync(
                    chat, _session!, cancellationToken: ct))
                {
                    sb.Append(update.Text);
                }
                return sb.ToString();
            });

        // Optional: log what Worker did for telemetry.
        logger.LogInformation(
            "WORKER: tasks={Tasks} satisfied={Sat} retries={Retries}",
            result.PreResult.TaskList.Tasks.Count,
            result.IsSatisfied,
            result.RetryCount);

        return message.Response(result.FinalResponse); // ProcessAsync sanitizes worker envelopes.
    }

    // Stub — replace with your real tool resolution.
    private Task<IEnumerable<AITool>> ResolveConfiguredToolsAsync() =>
        Task.FromResult<IEnumerable<AITool>>(Array.Empty<AITool>());
}
