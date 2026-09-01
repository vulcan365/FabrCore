using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A.Protocol;

/// <summary>Protocol-level constants for the A2A version this addon implements.</summary>
public static class A2AProtocol
{
    /// <summary>Value written to <see cref="A2AAgentCard.ProtocolVersion"/>.</summary>
    public const string Version = "0.3.0";

    // JSON-RPC method names.
    public const string MethodMessageSend = "message/send";
    public const string MethodMessageStream = "message/stream";
    public const string MethodTasksGet = "tasks/get";
    public const string MethodTasksCancel = "tasks/cancel";
    public const string MethodTasksResubscribe = "tasks/resubscribe";
    public const string MethodPushNotificationSet = "tasks/pushNotificationConfig/set";
    public const string MethodPushNotificationGet = "tasks/pushNotificationConfig/get";
    public const string MethodPushNotificationList = "tasks/pushNotificationConfig/list";
    public const string MethodPushNotificationDelete = "tasks/pushNotificationConfig/delete";
    public const string MethodAgentAuthenticatedExtendedCard = "agent/getAuthenticatedExtendedCard";
}

/// <summary>Shared serializer settings. A2A is camelCase and omits null members.</summary>
public static class A2AJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>An inbound JSON-RPC 2.0 request.</summary>
public sealed class A2AJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>Request id echoed on the response. Null for notifications.</summary>
    public JsonNode? Id { get; set; }

    public string Method { get; set; } = string.Empty;

    public JsonElement? Params { get; set; }
}

/// <summary>A JSON-RPC 2.0 response carrying either <see cref="Result"/> or <see cref="Error"/>.</summary>
public sealed class A2AJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    public JsonNode? Id { get; set; }

    public object? Result { get; set; }

    public A2AJsonRpcError? Error { get; set; }

    public static A2AJsonRpcResponse Success(JsonNode? id, object result)
        => new() { Id = id, Result = result };

    public static A2AJsonRpcResponse Failure(JsonNode? id, A2AJsonRpcError error)
        => new() { Id = id, Error = error };
}

/// <summary>A JSON-RPC 2.0 error object.</summary>
public sealed class A2AJsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

/// <summary>Error codes and factories for the JSON-RPC and A2A-specific error ranges.</summary>
public static class A2AErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int TaskNotFound = -32001;
    public const int TaskNotCancelable = -32002;
    public const int PushNotificationNotSupported = -32003;
    public const int UnsupportedOperation = -32004;
    public const int ContentTypeNotSupported = -32005;
    public const int InvalidAgentResponse = -32006;
    public const int AuthenticatedExtendedCardNotConfigured = -32007;

    public static A2AJsonRpcError Parse(string? detail = null)
        => new() { Code = ParseError, Message = "Invalid JSON payload", Data = detail };

    public static A2AJsonRpcError Invalid(string? detail = null)
        => new() { Code = InvalidRequest, Message = "Request payload validation error", Data = detail };

    public static A2AJsonRpcError MethodNotFoundFor(string method)
        => new() { Code = MethodNotFound, Message = "Method not found", Data = method };

    public static A2AJsonRpcError Params(string? detail = null)
        => new() { Code = InvalidParams, Message = "Invalid parameters", Data = detail };

    public static A2AJsonRpcError Internal(string? detail = null)
        => new() { Code = InternalError, Message = "Internal error", Data = detail };

    public static A2AJsonRpcError NoSuchTask(string taskId)
        => new() { Code = TaskNotFound, Message = "Task not found", Data = taskId };

    public static A2AJsonRpcError NotCancelable(string taskId)
        => new() { Code = TaskNotCancelable, Message = "Task cannot be canceled", Data = taskId };

    public static A2AJsonRpcError PushNotificationsUnsupported()
        => new() { Code = PushNotificationNotSupported, Message = "Push Notification is not supported" };

    public static A2AJsonRpcError Unsupported(string? detail = null)
        => new() { Code = UnsupportedOperation, Message = "This operation is not supported", Data = detail };

    /// <summary>
    /// HTTP status to use when an error is surfaced outside a JSON-RPC envelope (the REST binding
    /// reports failures with status codes rather than a 200 error envelope).
    /// </summary>
    public static int ToHttpStatus(int code) => code switch
    {
        ParseError or InvalidRequest or InvalidParams or ContentTypeNotSupported => StatusCodes.Status400BadRequest,
        MethodNotFound => StatusCodes.Status404NotFound,
        TaskNotFound => StatusCodes.Status404NotFound,
        TaskNotCancelable => StatusCodes.Status409Conflict,
        PushNotificationNotSupported or UnsupportedOperation => StatusCodes.Status501NotImplemented,
        _ => StatusCodes.Status500InternalServerError,
    };
}
