namespace FabrCore.Sdk;

/// <summary>
/// Principal-scoped typed storage shared by FabrCore modules that persist administration state.
/// Implementations must isolate values by principal, container, and entity key.
/// </summary>
public interface IPrincipalScopedFabrCoreStorageProvider
{
    Task<T?> GetAsync<T>(
        string principalId,
        string container,
        string entityKey,
        CancellationToken cancellationToken = default);

    Task UpsertAsync<T>(
        string principalId,
        string container,
        string entityKey,
        T value,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string principalId,
        string container,
        string entityKey,
        CancellationToken cancellationToken = default);
}
