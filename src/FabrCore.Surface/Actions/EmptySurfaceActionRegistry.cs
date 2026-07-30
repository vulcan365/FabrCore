namespace FabrCore.Surface.Actions;

public sealed class EmptySurfaceActionRegistry : ISurfaceActionRegistry
{
    public Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new SurfaceActionResult
        {
            Success = true,
            Message = "Action captured."
        });
}
