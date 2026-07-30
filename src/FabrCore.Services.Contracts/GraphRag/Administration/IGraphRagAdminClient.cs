using System.Net;
using FabrCore.Services.GraphRag.Administration.Models;
using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Administration;

public interface IGraphRagAdminClient : IGraphRagAdminService
{
    Task<GraphRagAdminCapability> GetCapabilityAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SourceDocumentDto>> ListDocumentsAsync(string? scopeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<int> CountDocumentsAsync(string? scopeFilter = null, CancellationToken ct = default);
    Task<SourceDocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<SourceDocumentDto> IngestDocumentAsync(GraphRagDocumentUpload upload, CancellationToken ct = default);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>
/// Supplies the current caller to an in-process administration client without
/// coupling the GraphRAG service package to a particular UI framework.
/// </summary>
public interface IGraphRagAdminPrincipalAccessor
{
    ValueTask<string?> GetPrincipalIdAsync(CancellationToken ct = default);
}

public sealed record GraphRagDocumentUpload(
    string FileName,
    string? ContentType,
    Stream Content,
    string ScopeKey,
    string? ExtractionInstructions = null);

public enum GraphRagAdminAvailability
{
    Available,
    Unregistered,
    Unavailable,
    Unreachable,
    Unauthorized
}

public sealed class GraphRagAdminCapability
{
    public const string CurrentApiVersion = "1";

    public GraphRagAdminAvailability Availability { get; init; }
    public string ApiVersion { get; init; } = CurrentApiVersion;
    public IReadOnlyList<string> Features { get; init; } = [];
    public string? Message { get; init; }

    public bool IsAvailable => Availability == GraphRagAdminAvailability.Available;
}

public sealed class GraphRagAdminClientException : Exception
{
    public GraphRagAdminClientException(
        string message,
        GraphRagAdminAvailability availability,
        string code,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Availability = availability;
        Code = code;
        StatusCode = statusCode;
    }

    public GraphRagAdminAvailability Availability { get; }
    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }
}

public static class GraphRagAdminProblemCodes
{
    public const string Unregistered = "graphrag_admin_unregistered";
    public const string Unavailable = "graphrag_admin_unavailable";
    public const string Unreachable = "graphrag_admin_unreachable";
    public const string Unauthorized = "graphrag_admin_unauthorized";
    public const string ValidationFailed = "graphrag_validation_failed";
    public const string Conflict = "graphrag_conflict";
    public const string NotFound = "graphrag_not_found";
}
