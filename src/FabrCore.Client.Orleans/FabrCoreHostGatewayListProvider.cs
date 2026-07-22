using FabrCore.Core.Connectivity;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;

namespace FabrCore.Client.Orleans;

/// <summary>
/// Orleans gateway provider which refreshes its list through the FabrCore Host API.
/// </summary>
public sealed class FabrCoreHostGatewayListProvider : IGatewayListProvider, IDisposable
{
    private readonly FabrCoreGatewayDiscoveryClient _discoveryClient;
    private readonly ILogger<FabrCoreHostGatewayListProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _clusterId;
    private readonly string _serviceId;
    private Uri[] _gateways;
    private long _maxStalenessTicks;
    private int _returnInitialList = 1;

    public FabrCoreHostGatewayListProvider(
        FabrCoreGatewayDiscoveryClient discoveryClient,
        FabrCoreGatewayDiscoveryDocument initialDocument,
        ILogger<FabrCoreHostGatewayListProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(initialDocument);
        ArgumentNullException.ThrowIfNull(logger);

        _discoveryClient = discoveryClient;
        _logger = logger;
        _clusterId = initialDocument.ClusterId;
        _serviceId = initialDocument.ServiceId;
        _gateways = discoveryClient.Validate(initialDocument).ToArray();
        _maxStalenessTicks = TimeSpan.FromSeconds(initialDocument.RefreshPeriodSeconds).Ticks;
    }

    public TimeSpan MaxStaleness => TimeSpan.FromTicks(Interlocked.Read(ref _maxStalenessTicks));

#pragma warning disable CS0618 // Required by Orleans 10 IGatewayListProvider for compatibility.
    public bool IsUpdatable => true;
#pragma warning restore CS0618

    public Task InitializeGatewayListProvider() => Task.CompletedTask;

    public async Task<IList<Uri>> GetGateways()
    {
        if (Interlocked.Exchange(ref _returnInitialList, 0) == 1)
        {
            return Snapshot();
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                var document = await _discoveryClient.GetGatewayDiscoveryAsync().ConfigureAwait(false);
                if (!string.Equals(document.ClusterId, _clusterId, StringComparison.Ordinal) ||
                    !string.Equals(document.ServiceId, _serviceId, StringComparison.Ordinal))
                {
                    throw new FabrCoreGatewayDiscoveryException(
                        "Gateway discovery returned a different clusterId or serviceId. An Orleans client cannot change cluster identity after startup.");
                }

                var refreshed = _discoveryClient.Validate(document).ToArray();
                Volatile.Write(ref _gateways, refreshed);
                Interlocked.Exchange(
                    ref _maxStalenessTicks,
                    TimeSpan.FromSeconds(document.RefreshPeriodSeconds).Ticks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to refresh Orleans gateways from {DiscoveryUri}; retaining the last valid list of {GatewayCount} gateways",
                    _discoveryClient.DiscoveryUri,
                    Volatile.Read(ref _gateways).Length);
            }

            return Snapshot();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private IList<Uri> Snapshot() => Volatile.Read(ref _gateways).ToArray();

    public void Dispose() => _refreshLock.Dispose();
}
