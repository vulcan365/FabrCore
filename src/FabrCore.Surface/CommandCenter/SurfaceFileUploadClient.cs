using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceFileUploadClient : ISurfaceFileUploadClient
{
    private readonly IFabrCoreHostApiClient hostApiClient;
    private readonly ILogger<SurfaceFileUploadClient> logger;

    public SurfaceFileUploadClient(
        IFabrCoreHostApiClient hostApiClient,
        ILogger<SurfaceFileUploadClient> logger)
    {
        this.hostApiClient = hostApiClient;
        this.logger = logger;
    }

    public async Task<SurfaceUploadedFile> UploadAsync(
        Stream stream,
        string fileName,
        string? contentType,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var ttlSeconds = Math.Max(1, (int)Math.Ceiling(ttl.TotalSeconds));
        logger.LogDebug("Uploading Surface chat file {FileName} with TTL {TtlSeconds}s.", fileName, ttlSeconds);

        var fileId = await hostApiClient.UploadFileAsync(stream, fileName, ttlSeconds, cancellationToken);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new InvalidOperationException("FabrCore host file upload did not return a file id.");
        }

        return new SurfaceUploadedFile(fileId, fileName, contentType);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return false;
        }

        logger.LogDebug("Deleting Surface chat file {FileId}.", fileId);
        var deleteFileAsyncMethod = hostApiClient.GetType().GetMethod(
            "DeleteFileAsync",
            [typeof(string), typeof(CancellationToken)]);
        if (deleteFileAsyncMethod is null)
        {
            logger.LogWarning(
                "FabrCore host API client does not expose DeleteFileAsync; abandoned Surface chat file {FileId} will expire by TTL.",
                fileId);
            return false;
        }

        var deleteTask = (Task<bool>?)deleteFileAsyncMethod.Invoke(hostApiClient, [fileId, cancellationToken]);
        return deleteTask is not null && await deleteTask;
    }
}
