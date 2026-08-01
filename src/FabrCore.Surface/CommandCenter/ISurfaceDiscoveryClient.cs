namespace FabrCore.Surface.CommandCenter;

public interface ISurfaceDiscoveryClient
{
    Task<SurfaceDiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken = default);
}
