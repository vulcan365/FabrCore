namespace FabrCore.Surface;

public interface ISurfaceClientContextFactory
{
    Task<ISurfaceClientContext> CreateAsync(string handle, CancellationToken cancellationToken = default);

    Task<ISurfaceClientContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(string handle);

    bool HasContext(string handle);
}
