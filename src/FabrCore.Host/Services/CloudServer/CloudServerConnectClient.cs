namespace FabrCore.Host.Services.CloudServer;

/// <summary>
/// Dedicated transport for the Cloud Server long-poll channel. It intentionally does not use
/// <see cref="IHttpClientFactory"/> because application-wide handler defaults (notably Aspire's
/// standard 10-second attempt timeout) are unsuitable for a request held open by the server.
/// Retry and timeout policy is owned by <see cref="CloudServerApiClient"/>.
/// </summary>
internal sealed class CloudServerConnectClient : IDisposable
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient client;

    public CloudServerConnectClient()
        : this(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = ConnectionLifetime
            },
            disposeHandler: true)
    {
    }

    internal CloudServerConnectClient(HttpMessageHandler handler, bool disposeHandler = false)
    {
        client = new HttpClient(handler, disposeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public void Dispose() => client.Dispose();
}
