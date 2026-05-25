namespace FabrCore.Surface.Services;

public sealed class SurfaceActionResult
{
    public bool Success { get; init; } = true;

    public string? Message { get; init; }

    public Dictionary<string, object?> Data { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
