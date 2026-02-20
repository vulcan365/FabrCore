namespace FabrCore.Host.Services
{
    public interface IFileStorageService
    {
        Task<string> SaveFileAsync(Stream fileStream, string fileExtension);
        void TrackFile(string fileId, DateTime expiresAt);
        void TrackFile(string fileId, string originalFileName, DateTime expiresAt);
        Task CleanupExpiredFilesAsync();
        Task<(Stream? fileStream, string? contentType)> GetFileAsync(string fileId);
        Task<FileMetadata?> GetFileMetadataAsync(string fileId);
    }
}
