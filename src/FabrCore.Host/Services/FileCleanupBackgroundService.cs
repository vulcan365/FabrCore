using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fabr.Host.Services
{
    public class FileCleanupBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly FileStorageSettings _settings;
        private readonly ILogger<FileCleanupBackgroundService> _logger;

        public FileCleanupBackgroundService(
            IServiceProvider serviceProvider,
            IOptions<FileStorageSettings> settings,
            ILogger<FileCleanupBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("File Cleanup Background Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_settings.CleanupIntervalMinutes), stoppingToken);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var fileStorageService = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
                        await fileStorageService.CleanupExpiredFilesAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("File Cleanup Background Service is stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in File Cleanup Background Service");
                }
            }
        }
    }
}
