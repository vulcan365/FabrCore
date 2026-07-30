namespace FabrCore.Surface.CommandCenter;

public interface ISurfaceMonitorClient
{
    Task<SurfaceMonitorTokenResponse> GetTokenSummariesAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorMessagesResponse> GetMessagesAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorEventsResponse> GetEventsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorLlmCallsResponse> GetLlmCallsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        bool failedOnly = false,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorErrorsResponse> GetErrorsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorConfigResponse> GetConfigAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task<SurfaceMonitorConfigResponse> UpdateConfigAsync(
        string principalId,
        SurfaceMonitorConfigUpdate update,
        CancellationToken cancellationToken = default);
}
