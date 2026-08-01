using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

[AgentAlias(SurfaceSwarmAgentTypes.Verifier)]
[Description("Built-in verifier for Surface Swarm squads.")]
[FabrCoreCapabilities("Judges Swarm task results against explicit acceptance criteria and returns structured pass/fail verdicts. Never plans or executes work.")]
[FabrCoreNote("Send swarm.verify.request messages with a SwarmVerifyPayload in Data; the verdict comes back as SwarmVerdict JSON in Data.")]
public sealed class SurfaceSwarmVerifierAgent : FabrCoreAgentProxy
{
    private SurfaceSwarmSquadRuntime runtime = new();
    private IChatClient? chatClient;
    private readonly ILogger<SurfaceSwarmVerifierAgent> verifierLogger;

    public SurfaceSwarmVerifierAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        verifierLogger = loggerFactory.CreateLogger<SurfaceSwarmVerifierAgent>();
    }

    public override async Task OnInitialize()
    {
        runtime = SurfaceSwarmSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        chatClient = await GetChatClient(BlankToDefault(config.Models));
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        response.MessageType = SurfaceSwarmMessageTypes.VerifyVerdict;

        if (message.MessageType != SurfaceSwarmMessageTypes.VerifyRequest)
        {
            response.Message = "Send a swarm.verify.request with a verify payload to get a verdict.";
            return response;
        }

        var payload = ReadPayload(message);
        if (payload is null)
        {
            SetVerdict(response, FailClosed("The verify request payload could not be parsed."));
            return response;
        }

        if (chatClient is null)
        {
            SetVerdict(response, FailClosed("The verifier is not initialized."));
            return response;
        }

        SetStatusMessage($"Verifying task {payload.TaskId}..");
        var verdict = await JudgeAsync(payload);
        SetStatusMessage(null);

        SetVerdict(response, verdict);
        return response;
    }

    private async Task<SwarmVerdict> JudgeAsync(SwarmVerifyPayload payload)
    {
        var criteria = payload.AcceptanceCriteria.Count > 0
            ? string.Join(Environment.NewLine, payload.AcceptanceCriteria.Select(criterion => $"- {criterion}"))
            : "- The task description is fully addressed by the result.";

        var strictNote = string.Equals(payload.VerificationDepth, "strict", StringComparison.OrdinalIgnoreCase)
            ? "Apply STRICT verification: every criterion must be explicitly and completely satisfied by concrete evidence in the result. When in doubt, fail."
            : "Apply reasonable verification: criteria must be substantively satisfied by the result.";

        var prompt = $$"""
            You are the verifier for the Swarm squad "{{runtime.Squad.Name}}".
            Judge ONLY whether the task result satisfies the acceptance criteria.
            Do not plan, do not suggest new tasks, and do not do the work yourself.
            {{strictNote}}

            Task:
            {{payload.Description}}

            Acceptance criteria:
            {{criteria}}

            Task result:
            {{payload.Result}}

            Return JSON: {"pass":true|false,"reasons":["per-criterion judgement"],"missingItems":["unmet criterion"],"retryGuidance":"concrete guidance for a retry, or null when pass"}
            """;

        try
        {
            var chatOptions = new ChatOptions
            {
                ResponseFormat = SwarmSchema.For<SwarmVerifierVerdict>(
                    "SwarmVerifierVerdict",
                    "Structured verdict judging a task result against acceptance criteria")
            };

            var result = await chatClient!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                chatOptions);
            var text = result.Text ?? string.Empty;
            var verdict = SwarmVerifierVerdict.Parse(text);
            if (verdict is null)
            {
                verifierLogger.LogWarning(
                    "Swarm verifier output could not be parsed - Handle: {Handle}, TaskId: {TaskId}, ResponsePreview: {Preview}",
                    fabrcoreAgentHost.GetHandle(),
                    payload.TaskId,
                    Truncate(text, 500));
                return FailClosed("The verifier output was unparseable.");
            }

            return verdict.ToVerdict();
        }
        catch (Exception ex)
        {
            verifierLogger.LogWarning(
                ex,
                "Swarm verifier LLM call failed - Handle: {Handle}, TaskId: {TaskId}",
                fabrcoreAgentHost.GetHandle(),
                payload.TaskId);
            return FailClosed($"The verifier could not evaluate the result: {ex.Message}");
        }
    }

    private static SwarmVerifyPayload? ReadPayload(AgentMessage message)
    {
        var json = message.Data is { Length: > 0 }
            ? Encoding.UTF8.GetString(message.Data)
            : message.Message;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SwarmVerifyPayload>(json, SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SetVerdict(AgentMessage response, SwarmVerdict verdict)
    {
        response.DataType = SurfaceSwarmDataTypes.Verdict;
        response.Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(verdict));
        response.Message = verdict.Pass
            ? "Pass"
            : $"Fail: {string.Join("; ", verdict.Reasons)}";
    }

    private static SwarmVerdict FailClosed(string reason)
        => new()
        {
            Pass = false,
            Reasons = [reason],
            MissingItems = [],
            RetryGuidance = null
        };

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}
