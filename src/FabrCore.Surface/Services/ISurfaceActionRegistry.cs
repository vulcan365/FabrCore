namespace FabrCore.Surface.Services;

public interface ISurfaceActionRegistry
{
    Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default);
}
