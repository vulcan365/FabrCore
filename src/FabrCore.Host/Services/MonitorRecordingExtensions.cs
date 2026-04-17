using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Fire-and-forget helpers for IAgentMessageMonitor recording calls.
    /// The in-memory default implementation completes synchronously, but pluggable
    /// monitors (Redis, Kafka, etc.) may do network IO — silent failures would
    /// make monitor drift invisible, so we attach a logged+metric'd continuation.
    /// </summary>
    internal static class MonitorRecordingExtensions
    {
        /// <summary>
        /// Attach a fault-only continuation that logs the exception and increments
        /// the supplied error counter with <c>error.type=monitor_record_failed</c>
        /// and the given <paramref name="operation"/> tag. Safe to call on an already-
        /// completed task.
        /// </summary>
        public static void TrackRecording(
            this Task recordingTask,
            ILogger logger,
            Counter<long> errorCounter,
            string operation,
            string? agentHandle = null)
        {
            if (recordingTask.IsCompletedSuccessfully)
            {
                return;
            }

            recordingTask.ContinueWith(
                t =>
                {
                    if (t.Exception is null) return;

                    logger.LogWarning(
                        t.Exception.Flatten(),
                        "Monitor recording failed for {Operation} on {AgentHandle}",
                        operation,
                        agentHandle ?? "(unknown)");

                    errorCounter.Add(1,
                        new KeyValuePair<string, object?>("error.type", "monitor_record_failed"),
                        new KeyValuePair<string, object?>("operation", operation));
                },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
