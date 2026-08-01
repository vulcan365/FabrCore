using FabrCore.Core;
using System.ClientModel.Primitives;

namespace FabrCore.Sdk
{
    /// <summary>
    /// Opt-in pipeline policy that stamps outbound LLM requests with FabrCore attribution
    /// headers (agent handle, trace id, origin) read from the ambient <see cref="LlmUsageScope"/>
    /// / <see cref="LlmCallContext"/> at request time. Enables OpenAI-compatible gateways
    /// (e.g. Forge Gateway) to meter and limit per agent. Enabled via the
    /// <c>FabrCore:EmitAttributionHeaders</c> configuration flag; when no ambient context is
    /// active the request is sent without attribution headers.
    /// </summary>
    public sealed class AgentAttributionPipelinePolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ApplyHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ApplyHeaders(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private static void ApplyHeaders(PipelineMessage message)
        {
            // Same resolution order as TokenTrackingChatClient: the usage scope carries the
            // OnMessage context; an LlmCallContext overrides the origin for nested work.
            var scope = LlmUsageScope.Current;
            var ctx = LlmCallContext.Current;

            var handle = scope?.AgentHandle ?? ctx?.AgentHandle;
            var traceId = scope?.TraceId ?? ctx?.TraceId;
            var origin =
                ctx?.OriginContext
                ?? scope?.OriginContext
                ?? (scope is not null ? "OnMessage" : null);

            SetHeader(message, AttributionHeaders.AgentHandle, handle);
            SetHeader(message, AttributionHeaders.TraceId, traceId);
            SetHeader(message, AttributionHeaders.Origin, origin);
        }

        private static void SetHeader(PipelineMessage message, string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            // Header values must not contain control characters; handles and origins are
            // caller-supplied strings, so strip anything that would break the header.
            if (value.Any(c => c < ' ' || c > '~'))
            {
                value = new string(value.Where(c => c >= ' ' && c <= '~').ToArray());
                if (value.Length == 0)
                {
                    return;
                }
            }

            message.Request.Headers.Set(name, value);
        }
    }
}
