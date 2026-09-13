using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Administration;

public sealed record MemoryScopeCreateRequest(string? Description);

public sealed record MemoryCreateRequest(
    string Title,
    MemoryType Type,
    string Content,
    string? Description = null,
    MemoryTemperature Temperature = MemoryTemperature.Warm,
    bool IsPointInTime = false,
    Dictionary<string, string>? Metadata = null);

public sealed record MemoryUpdateRequest(
    string Title,
    MemoryType Type,
    string Content,
    string? Description,
    MemoryTemperature Temperature);
