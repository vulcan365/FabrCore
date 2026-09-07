using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Models;

// Call these methods from OnMessage or other application-controlled agent methods.
public static class MemoryLifecycle
{
    public static async Task<Guid> RememberPreferenceAsync(IAgentMemoryService memory, CancellationToken ct = default)
    {
        var entry = await memory.SaveMemoryAsync("Response preference", MemoryType.Instruction,
            "The user prefers concise responses.", metadata: new() { ["source"] = "explicit-user-preference" }, ct: ct);
        return entry.Id; // Persist this ID in application state if later updates need it.
    }

    public static Task<MemoryEntry> ArchiveAsync(IAgentMemoryService memory, Guid id, CancellationToken ct = default)
        => memory.UpdateMemoryAsync(id, temperature: MemoryTemperature.Cold, ct: ct);

    public static Task<MemoryEntry> RestoreAsync(IAgentMemoryService memory, Guid id, CancellationToken ct = default)
        => memory.UpdateMemoryAsync(id, temperature: MemoryTemperature.Warm, ct: ct);

    public static async Task<string> RecallAsync(IAgentMemoryService memory, string query, CancellationToken ct = default)
        => memory.FormatRecallContext(await memory.RecallAsync(query, ct: ct));
}
