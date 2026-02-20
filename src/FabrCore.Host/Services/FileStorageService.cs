using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Fabr.Host.Services
{
    public class FileStorageService : IFileStorageService
    {
        private readonly FileStorageSettings _settings;
        private readonly ConcurrentDictionary<string, DateTime> _fileTtlTracker;
        private readonly ConcurrentDictionary<string, string> _fileNameTracker;
        private readonly ILogger<FileStorageService> _logger;

        public FileStorageService(IOptions<FileStorageSettings> settings, ILogger<FileStorageService> logger)
        {
            _settings = settings.Value;
            _logger = logger;
            _fileTtlTracker = new ConcurrentDictionary<string, DateTime>();
            _fileNameTracker = new ConcurrentDictionary<string, string>();

            // Ensure storage directory exists
            if (!Directory.Exists(_settings.StoragePath))
            {
                Directory.CreateDirectory(_settings.StoragePath);
                _logger.LogInformation($"Created storage directory: {_settings.StoragePath}");
            }
        }

        public async Task<string> SaveFileAsync(Stream fileStream, string fileExtension)
        {
            var fileId = Guid.NewGuid().ToString();
            var fileName = $"{fileId}{fileExtension}";
            var filePath = Path.Combine(_settings.StoragePath, fileName);

            using (var fileStreamOutput = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                await fileStream.CopyToAsync(fileStreamOutput);
            }

            _logger.LogInformation($"File saved: {fileName}");
            return fileId;
        }

        public void TrackFile(string fileId, DateTime expiresAt)
        {
            _fileTtlTracker[fileId] = expiresAt;
            _logger.LogDebug($"Tracking file {fileId} with expiration at {expiresAt}");
        }

        public void TrackFile(string fileId, string originalFileName, DateTime expiresAt)
        {
            _fileTtlTracker[fileId] = expiresAt;
            _fileNameTracker[fileId] = originalFileName;
            _logger.LogDebug($"Tracking file {fileId} ({originalFileName}) with expiration at {expiresAt}");
        }

        public async Task CleanupExpiredFilesAsync()
        {
            var now = DateTime.UtcNow;
            var expiredFiles = _fileTtlTracker.Where(kvp => kvp.Value <= now).ToList();

            foreach (var expiredFile in expiredFiles)
            {
                var pattern = $"{expiredFile.Key}.*";
                var files = Directory.GetFiles(_settings.StoragePath, pattern);

                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation($"Deleted expired file: {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting file: {file}");
                    }
                }

                _fileTtlTracker.TryRemove(expiredFile.Key, out _);
                _fileNameTracker.TryRemove(expiredFile.Key, out _);
            }

            // Clean up orphaned files (files not in TTL tracker)
            await CleanupOrphanedFilesAsync();
        }

        public async Task<(Stream? fileStream, string? contentType)> GetFileAsync(string fileId)
        {
            var files = Directory.GetFiles(_settings.StoragePath, $"{fileId}.*");

            if (files.Length == 0)
            {
                _logger.LogWarning($"File not found: {fileId}");
                return (null, null);
            }

            var filePath = files[0];
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _logger.LogInformation($"File retrieved: {Path.GetFileName(filePath)}");

            return await Task.FromResult((fileStream, contentType));
        }

        public async Task<FileMetadata?> GetFileMetadataAsync(string fileId)
        {
            var files = Directory.GetFiles(_settings.StoragePath, $"{fileId}.*");

            if (files.Length == 0)
            {
                _logger.LogWarning($"File metadata not found: {fileId}");
                return null;
            }

            var filePath = files[0];
            var fileInfo = new FileInfo(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };

            _fileNameTracker.TryGetValue(fileId, out var originalFileName);
            _fileTtlTracker.TryGetValue(fileId, out var expiresAt);

            var metadata = new FileMetadata
            {
                FileId = fileId,
                OriginalFileName = originalFileName ?? Path.GetFileName(filePath),
                ExpiresAt = expiresAt,
                FileSize = fileInfo.Length,
                ContentType = contentType
            };

            _logger.LogInformation($"File metadata retrieved: {fileId}");
            return await Task.FromResult(metadata);
        }

        private async Task CleanupOrphanedFilesAsync()
        {
            await Task.Run(() =>
            {
                var allFiles = Directory.GetFiles(_settings.StoragePath);
                var trackedFileNames = _fileTtlTracker.Keys
                    .SelectMany(fileId => Directory.GetFiles(_settings.StoragePath, $"{fileId}.*"))
                    .Select(f => Path.GetFileName(f))
                    .ToHashSet();

                foreach (var filePath in allFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!trackedFileNames.Contains(fileName))
                    {
                        try
                        {
                            File.Delete(filePath);
                            _logger.LogInformation($"Deleted orphaned file: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error deleting orphaned file: {filePath}");
                        }
                    }
                }
            });
        }
    }
}
