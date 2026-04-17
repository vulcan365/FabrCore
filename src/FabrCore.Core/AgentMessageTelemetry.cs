using System.Diagnostics;

namespace FabrCore.Core
{
    /// <summary>
    /// Helpers that bridge <see cref="AgentMessage"/> with <see cref="System.Diagnostics.Activity"/>
    /// so message-carried trace context (W3C TraceContext format) can parent runtime spans
    /// and runtime spans can stamp outbound messages.
    /// </summary>
    public static class AgentMessageTelemetry
    {
        /// <summary>
        /// Tries to build an <see cref="ActivityContext"/> from the message's TraceId/SpanId.
        /// Returns false if either is missing or not valid W3C hex.
        /// </summary>
        public static bool TryGetParentContext(this AgentMessage message, out ActivityContext context)
        {
            context = default;
            if (string.IsNullOrEmpty(message.TraceId) || string.IsNullOrEmpty(message.SpanId))
                return false;

            try
            {
                var traceId = ActivityTraceId.CreateFromString(message.TraceId.AsSpan());
                var spanId = ActivitySpanId.CreateFromString(message.SpanId.AsSpan());
                context = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded, isRemote: true);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (FormatException) { return false; }
        }

        /// <summary>
        /// Copies TraceId/SpanId/ParentSpanId from a live <see cref="Activity"/> onto the message.
        /// No-op if the activity is null.
        /// </summary>
        public static void StampFromActivity(this AgentMessage message, Activity? activity)
        {
            if (activity is null) return;

            message.TraceId = activity.TraceId.ToHexString();
            message.SpanId = activity.SpanId.ToHexString();
            message.ParentSpanId = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString();
        }

        /// <summary>
        /// Starts an ingress <see cref="Activity"/> for a message that just crossed into a new component.
        /// Parent context precedence: (1) the message's own TraceId/SpanId, (2) <paramref name="outerParent"/>
        /// (e.g. extracted from a traceparent header), (3) none (new root).
        /// If the message had no TraceId before, the new Activity's ids are stamped back onto it so
        /// downstream hops can parent on it.
        /// </summary>
        public static Activity? StartIngressActivity(
            this AgentMessage message,
            ActivitySource source,
            string name,
            ActivityKind kind,
            ActivityContext outerParent = default)
        {
            var hadTraceId = !string.IsNullOrEmpty(message.TraceId);

            ActivityContext parent;
            if (message.TryGetParentContext(out var msgCtx))
                parent = msgCtx;
            else if (outerParent != default)
                parent = outerParent;
            else
                parent = default;

            var activity = source.StartActivity(name, kind, parent);

            if (activity is not null && !hadTraceId)
            {
                message.StampFromActivity(activity);
            }

            return activity;
        }
    }
}
