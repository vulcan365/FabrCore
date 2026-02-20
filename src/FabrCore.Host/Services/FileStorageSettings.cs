namespace FabrCore.Host.Services
{
    public class FileStorageSettings
    {
        public string StoragePath { get; set; } = "c:\\temp\\fabrcorefiles";
        public int DefaultTtlSeconds { get; set; } = 300; // 5 minutes
        public int CleanupIntervalMinutes { get; set; } = 1;
    }
}
