using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

// Add `#pragma warning disable MAAI001` at the top of this file only if you name the upstream
// harness types directly — TodoProvider, LoopAgent, BackgroundAgentsProvider, or the loop
// evaluators. The FabrCore surface used below needs no suppression.

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// </summary>
/// <remarks>
/// A harness agent: the model keeps its own todo list, the loop re-invokes it until that list is
/// clear, and it delegates work to the agents named in the <c>_HarnessBackgroundAgents</c> arg.
/// See the fabrcore-harness skill for the full configuration surface.
/// </remarks>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
[FabrCoreCapabilities("{{AGENT_CAPABILITIES}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private const string ThreadId = "main";

    private FabrCoreHarnessResult harness = null!;

    // Health metrics are collected synchronously, so plan counts are cached at the end of each turn
    // rather than read live. GetRemainingTodosAsync takes a per-session lock that the in-flight run
    // may already hold — blocking on it inside a grain turn risks a deadlock.
    private int todoTotal;
    private int todoRemaining;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    /// <summary>
    /// Runs on every grain activation. The harness restores its persisted session here, so todos
    /// and delegation records from earlier turns are already in place when OnMessage runs.
    /// </summary>
    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();

        harness = await CreateFabrCoreHarnessAgent(
            config.Models ?? "default",
            ThreadId,
            tools);

        // Anything not expressible as a _Harness* arg goes in the configure callback, which runs
        // after args are read and wins over them:
        //
        // harness = await CreateFabrCoreHarnessAgent(
        //     config.Models ?? "default",
        //     ThreadId,
        //     tools,
        //     options =>
        //     {
        //         options.LoopMode = HarnessLoopMode.Todo | HarnessLoopMode.Background;
        //         options.LoopMaxIterations = 12;
        //         options.HarnessInstructions = FabrCoreHarnessAgent.DefaultInstructions
        //             + "\n\nAlways cite the runbook section you acted on.";
        //     });
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            response.Message = "Send a goal to work on.";
            return response;
        }

        // The 3s heartbeat shows this to the caller while the loop runs.
        SetStatusMessage("Planning...");

        string text;
        try
        {
            // ALWAYS run through the result, never harness.Agent.RunAsync — this wrapper snapshots
            // the session afterwards, and that snapshot is what carries todos across turns.
            var run = await harness.RunAsync(message.Message);
            text = string.IsNullOrWhiteSpace(run.Text)
                ? "Finished, but produced no summary."
                : run.Text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Harness run failed - Handle: {Handle}", fabrcoreAgentHost.GetHandle());
            response.Message = $"Could not finish this goal: {ex.Message}";
            return response;
        }
        finally
        {
            SetStatusMessage(string.Empty);
        }

        response.Message = await AppendUnfinishedWorkAsync(text);
        return response;
    }

    /// <summary>
    /// Reports what did not get done, and refreshes the cached plan counts. The iteration cap is a
    /// budget, not a guarantee, and delegations in flight before a restart cannot be recovered —
    /// say so rather than returning a summary that reads like success.
    /// </summary>
    private async Task<string> AppendUnfinishedWorkAsync(string text)
    {
        if (harness.DescribeLostDelegations() is { } lost)
        {
            text += $"{Environment.NewLine}{Environment.NewLine}{lost}";
        }

        var all = await harness.GetAllTodosAsync();
        todoTotal = all.Count;
        todoRemaining = all.Count(item => !item.IsComplete);

        if (todoRemaining > 0)
        {
            var unfinished = all.Where(item => !item.IsComplete).Select(item => $"- {item.Title}");
            text += $"{Environment.NewLine}{Environment.NewLine}Not completed within the iteration budget:{Environment.NewLine}"
                + string.Join(Environment.NewLine, unfinished);
        }

        return text;
    }

    /// <summary>
    /// Surfaces plan progress to health probes and dashboards — the point of the model keeping its
    /// work list in typed state rather than in prose.
    /// </summary>
    protected override Dictionary<string, string>? GetCustomHealthMetrics(HealthDetailLevel detailLevel)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TodoTotal"] = todoTotal.ToString(),
            ["TodoRemaining"] = todoRemaining.ToString(),
            ["DelegationsRunning"] = harness.GetRunningDelegations().Count.ToString(),
            ["DelegationsLostOnRestore"] = harness.DelegationsLostOnRestore.ToString(),
            ["SessionRestored"] = harness.SessionRestored.ToString(),
            ["SessionPersistent"] = harness.IsSessionPersistent.ToString()
        };
}

// Example blueprint entry — see assets/harness-blueprint.json for the full document:
//
// {
//   "handle": "{{AGENT_ALIAS}}",
//   "agentType": "{{AGENT_ALIAS}}",
//   "models": "default",
//   "systemPrompt": "{{AGENT_SYSTEM_PROMPT}}",
//   "args": {
//     "_HarnessLoop": "todo,background",
//     "_HarnessLoopMaxIterations": "8",
//     "_HarnessBackgroundAgents": "crm,policy-desk"
//   }
// }
