using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Actions;

public interface ISurfaceActionDispatcher
{
    Task DispatchAsync(
        SurfaceActionContext context,
        AdaptiveCardSurfaceAction action,
        CancellationToken cancellationToken = default);
}
