using System.Net;

namespace FabrCore.Services.Memory.Administration;

/// <summary>
/// Transport-neutral client surface for local or remote Memory administration.
/// </summary>
public interface IMemoryAdminClient : IMemoryAdminService
{
    Task<MemoryAdminCapability> GetCapabilityAsync(CancellationToken ct = default);
}

public interface IMemoryAdminPrincipalAccessor
{
    ValueTask<string?> GetPrincipalIdAsync(CancellationToken ct = default);
}

public enum MemoryAdminAvailability
{
    Available,
    Unregistered,
    Unavailable,
    Unreachable,
    Unauthorized
}

public sealed class MemoryAdminCapability
{
    public const string CurrentApiVersion = "1";

    public MemoryAdminAvailability Availability { get; init; }
    public string ApiVersion { get; init; } = CurrentApiVersion;
    public IReadOnlyList<string> Features { get; init; } = [];
    public string? Message { get; init; }

    public bool IsAvailable => Availability == MemoryAdminAvailability.Available;
}

public sealed class MemoryAdminClientException : Exception
{
    public MemoryAdminClientException(
        string message,
        MemoryAdminAvailability availability,
        string code,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Availability = availability;
        Code = code;
        StatusCode = statusCode;
    }

    public MemoryAdminAvailability Availability { get; }
    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }
}

public static class MemoryAdminProblemCodes
{
    public const string Unregistered = "memory_admin_unregistered";
    public const string Unavailable = "memory_admin_unavailable";
    public const string Unreachable = "memory_admin_unreachable";
    public const string Unauthorized = "memory_admin_unauthorized";
    public const string ValidationFailed = "memory_validation_failed";
    public const string Conflict = "memory_conflict";
    public const string NotFound = "memory_not_found";
}

public static class MemoryAdminClientKeys
{
    public const string Local = "FabrCore.Memory.Admin.Local";
}
