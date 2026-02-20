namespace Fabr.Host.Services
{
    public class FileMetadata
    {
        public string FileId { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public long FileSize { get; set; }
        public string ContentType { get; set; } = string.Empty;
    }
}
