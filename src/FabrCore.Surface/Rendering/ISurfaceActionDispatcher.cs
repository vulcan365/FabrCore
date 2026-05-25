using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Rendering;

public interface ISurfaceActionDispatcher
{
    Task DispatchAsync(
        SurfaceRenderContext context,
        AdaptiveCardSurfaceAction action,
        CancellationToken cancellationToken = default);
}
