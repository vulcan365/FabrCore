using Fabr.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Fabr.Host.Services
{
    public class AgentRegistryCleanupService : BackgroundService
    {
        private readonly IClusterClient _clusterClient;
        private readonly ILogger<AgentRegistryCleanupService> _logger;
        private readonly TimeSpan _purgeInterval = TimeSpan.FromHours(6);
        private readonly TimeSpan _purgeAge = TimeSpan.FromDays(7);

        public AgentRegistryCleanupService(
            IClusterClient clusterClient,
            ILogger<AgentRegistryCleanupService> logger)
        {
            _clusterClient = clusterClient;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Agent Registry Cleanup Service started - Interval: {Interval}, Age: {Age}",
                _purgeInterval, _purgeAge);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_purgeInterval, stoppingToken);

                    _logger.LogInformation("Running agent registry cleanup");

                    var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);
                    var purgedCount = await registry.PurgeDeactivatedAgentsOlderThan(_purgeAge);

                    if (purgedCount > 0)
                    {
                        _logger.LogInformation(
                            "Automatic cleanup purged {Count} agents older than {Age}",
                            purgedCount, _purgeAge);
                    }
                    else
                    {
                        _logger.LogDebug("No agents to purge");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Agent Registry Cleanup Service is stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during agent registry cleanup");
                }
            }

            _logger.LogInformation("Agent Registry Cleanup Service stopped");
        }
    }
}
