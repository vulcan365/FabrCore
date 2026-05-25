namespace FabrCore.Surface.Services;

public sealed class EmptySurfaceActionRegistry : ISurfaceActionRegistry
{
    public Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new SurfaceActionResult
        {
            Success = true,
            Message = "Action captured."
        });
}
