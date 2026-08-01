using System.IO;

namespace FabrCore.Surface.CommandCenter;

public interface ISurfaceFileUploadClient
{
    Task<SurfaceUploadedFile> UploadAsync(
        Stream stream,
        string fileName,
        string? contentType,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default);
}
