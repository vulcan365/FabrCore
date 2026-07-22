using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Core.Connectivity;

namespace FabrCore.Client.Orleans;

/// <summary>Fetches and validates Orleans gateway discovery documents.</summary>
public sealed class FabrCoreGatewayDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _discoveryUri;
    private readonly bool _allowInsecureOrleansTransport;

    public FabrCoreGatewayDiscoveryClient(
        HttpClient httpClient,
        FabrCoreOrleansClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _discoveryUri = options.GetDiscoveryUri();
        _allowInsecureOrleansTransport = options.AllowInsecureOrleansTransport;
    }

    /// <summary>Gets the absolute discovery endpoint used by this client.</summary>
    public Uri DiscoveryUri => _discoveryUri;

    /// <summary>Fetches and validates the current discovery document.</summary>
    public async Task<FabrCoreGatewayDiscoveryDocument> GetGatewayDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _discoveryUri);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FabrCoreGatewayDiscoveryException(
                $"Unable to retrieve FabrCore gateway discovery from '{_discoveryUri}'.",
                innerException: ex);
        }

        using (response)
        {

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (detail.Length > 1024)
                {
                    detail = detail[..1024];
                }

                throw new FabrCoreGatewayDiscoveryException(
                    $"FabrCore gateway discovery at '{_discoveryUri}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})." +
                    (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Response: {detail}"),
                    response.StatusCode);
            }

            FabrCoreGatewayDiscoveryDocument? document;
            try
            {
                document = await response.Content
                    .ReadFromJsonAsync<FabrCoreGatewayDiscoveryDocument>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new FabrCoreGatewayDiscoveryException(
                    $"FabrCore gateway discovery at '{_discoveryUri}' returned invalid JSON.",
                    response.StatusCode,
                    ex);
            }

            GatewayDiscoveryDocumentValidator.Validate(document, _allowInsecureOrleansTransport);
            return document!;
        }
    }

    internal IReadOnlyList<Uri> Validate(FabrCoreGatewayDiscoveryDocument document)
        => GatewayDiscoveryDocumentValidator.Validate(document, _allowInsecureOrleansTransport);
}
