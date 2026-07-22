using System.Net;

namespace FabrCore.Client.Orleans;

/// <summary>Represents an authentication, transport, or contract failure during gateway discovery.</summary>
public sealed class FabrCoreGatewayDiscoveryException : InvalidOperationException
{
    public FabrCoreGatewayDiscoveryException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the HTTP status code, when the failure came from an HTTP response.</summary>
    public HttpStatusCode? StatusCode { get; }
}
