using System.Text.Json;

namespace FabrCore.Sdk;

/// <summary>
/// Durable envelope for a harness <c>AgentSession</c>, stored in agent custom state so todos and
/// delegation records survive grain deactivation.
/// </summary>
/// <remarks>
/// The payload holds only <c>{ conversationId, stateBag }</c>. Conversation history is not in it —
/// <see cref="FabrCoreChatHistoryProvider"/> persists messages to Orleans <c>MessageThreads</c> instead of
/// the session state bag, which keeps snapshots at kilobyte scale and means a lost snapshot never costs
/// conversation continuity.
/// </remarks>
public sealed class HarnessSessionSnapshot
{
    /// <summary>Envelope version. Bumped when the stored shape changes incompatibly.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The envelope version this snapshot was written with.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The conversation thread the snapshot belongs to.</summary>
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>When the snapshot was taken.</summary>
    public DateTimeOffset SavedUtc { get; set; }

    /// <summary>The serialized session produced by <c>AIAgent.SerializeSessionAsync</c>.</summary>
    public JsonElement Payload { get; set; }

    /// <summary>Builds the custom-state key a thread's snapshot is stored under.</summary>
    public static string KeyFor(string threadId) => $"_harness_session:{threadId}";

    /// <summary>Builds the custom-state key an unreadable snapshot is archived under.</summary>
    public static string CorruptKeyFor(string threadId) => $"_harness_session_corrupt:{threadId}";

    /// <summary>
    /// Counts delegations that were still running when the snapshot was taken. Those tasks cannot survive:
    /// the provider's runtime state holds live <c>Task</c> and child-session objects behind
    /// <c>[JsonIgnore]</c>, so a restored provider marks every one of them <c>Lost</c> on its next refresh.
    /// </summary>
    /// <remarks>
    /// The provider exposes no reader for lost tasks, so this reads the snapshot's own JSON. It is written
    /// defensively on purpose — an upstream shape change degrades this to reporting zero, never to a failure.
    /// </remarks>
    internal static int CountRunningDelegations(JsonElement payload)
    {
        try
        {
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("stateBag", out var stateBag)
                || stateBag.ValueKind != JsonValueKind.Object
                || !stateBag.TryGetProperty("BackgroundAgentsProvider", out var providerState)
                || providerState.ValueKind != JsonValueKind.Object
                || !providerState.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var running = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty("status", out var status))
                {
                    continue;
                }

                // The enum serializes as a number or a name depending on the converter in play; accept both.
                var isRunning = status.ValueKind switch
                {
                    JsonValueKind.Number => status.TryGetInt32(out var value) && value == 0,
                    JsonValueKind.String => string.Equals(status.GetString(), "Running", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (isRunning)
                {
                    running++;
                }
            }

            return running;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

/// <summary>
/// Persistence sink for harness session snapshots.
/// </summary>
/// <remarks>
/// Implemented by <see cref="FabrCoreAgentProxy"/> over its custom-state API, which is protected and so
/// unreachable from <see cref="FabrCoreHarnessResult"/> directly. Substituting an implementation is what
/// makes the harness testable without a grain.
/// </remarks>
public interface IHarnessSessionStore
{
    /// <summary>Writes a snapshot and flushes it to durable storage.</summary>
    Task WriteAsync(string key, HarnessSessionSnapshot snapshot);

    /// <summary>Removes a snapshot and flushes the removal.</summary>
    Task DeleteAsync(string key);
}
