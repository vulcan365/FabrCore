using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Readiness probe backing <c>/health/ready</c>. Reports Healthy once the
    /// Orleans cluster client can produce a grain reference. Without this, a
    /// pod that is up but not yet joined to the cluster would accept traffic
    /// and fail grain calls immediately — the classic rolling-deploy footgun.
    /// </summary>
    internal sealed class OrleansClusterHealthCheck : IHealthCheck
    {
        private readonly IClusterClient _clusterClient;

        public OrleansClusterHealthCheck(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // GetGrain is a reference-creation call, not a network roundtrip —
                // but it fails fast if the cluster client factory is not initialized.
                _ = _clusterClient.GetGrain<IAgentManagementGrain>(0);
                return Task.FromResult(HealthCheckResult.Healthy("Orleans cluster client ready"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Orleans cluster client not ready", ex));
            }
        }
    }
}
