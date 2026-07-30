namespace FabrCore.Surface.Actions;

public interface ISurfaceActionRegistry
{
    Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default);
}
