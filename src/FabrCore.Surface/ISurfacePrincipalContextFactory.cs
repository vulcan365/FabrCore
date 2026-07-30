namespace FabrCore.Surface;

public interface ISurfacePrincipalContextFactory
{
    Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default);

    Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(string handle);

    bool HasContext(string handle);
}
