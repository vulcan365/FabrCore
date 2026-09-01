using System.Text.Json;
using System.Text.Json.Serialization;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A.Protocol;

// Wire types for A2A protocol version 0.3.0 (https://a2a-protocol.org). Property names are
// serialized camelCase by A2AJson.Options, which matches the published JSON schema exactly.
//
// Union types in the schema (Part, SendMessageResponse result, streaming events) are modelled as
// single classes carrying every member plus the schema's "kind" discriminator, and null members
// are omitted on write. That produces byte-identical payloads without polymorphic converters, and
// it keeps deserialization tolerant of the shape variations real clients send.

/// <summary>A single piece of content inside a message or artifact.</summary>
public sealed class A2APart
{
    /// <summary>Discriminator: <c>text</c>, <c>data</c>, or <c>file</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = A2APartKinds.Text;

    /// <summary>Content for <c>kind = text</c>.</summary>
    public string? Text { get; set; }

    /// <summary>Structured payload for <c>kind = data</c>.</summary>
    public JsonElement? Data { get; set; }

    /// <summary>Reference or inline bytes for <c>kind = file</c>.</summary>
    public A2AFile? File { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public static A2APart FromText(string text) => new() { Kind = A2APartKinds.Text, Text = text };

    public static A2APart FromData(JsonElement data) => new() { Kind = A2APartKinds.Data, Data = data };
}

/// <summary>Discriminator values for <see cref="A2APart.Kind"/>.</summary>
public static class A2APartKinds
{
    public const string Text = "text";
    public const string Data = "data";
    public const string File = "file";
}

/// <summary>File content carried by a <c>file</c> part: either inline base64 bytes or a URI.</summary>
public sealed class A2AFile
{
    public string? Name { get; set; }
    public string? MimeType { get; set; }

    /// <summary>Base64-encoded content (<c>FileWithBytes</c>).</summary>
    public string? Bytes { get; set; }

    /// <summary>Location of the content (<c>FileWithUri</c>).</summary>
    public string? Uri { get; set; }
}

/// <summary>A turn in an A2A conversation.</summary>
public sealed class A2AMessage
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary><c>user</c> for client turns, <c>agent</c> for ours.</summary>
    public string Role { get; set; } = A2ARoles.User;

    public List<A2APart> Parts { get; set; } = new();

    public string? ContextId { get; set; }
    public string? TaskId { get; set; }
    public List<string>? ReferenceTaskIds { get; set; }
    public List<string>? Extensions { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Values for <see cref="A2AMessage.Role"/>.</summary>
public static class A2ARoles
{
    public const string User = "user";
    public const string Agent = "agent";
}

/// <summary>Lifecycle state of a task. Terminal states end the stream.</summary>
public static class A2ATaskStates
{
    public const string Submitted = "submitted";
    public const string Working = "working";
    public const string InputRequired = "input-required";
    public const string Completed = "completed";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string AuthRequired = "auth-required";
    public const string Unknown = "unknown";

    /// <summary>True when no further updates can follow this state.</summary>
    public static bool IsTerminal(string? state) => state is Completed or Canceled or Failed or Rejected;
}

/// <summary>Current state of a task plus the message that produced it.</summary>
public sealed class A2ATaskStatus
{
    public string State { get; set; } = A2ATaskStates.Submitted;
    public A2AMessage? Message { get; set; }

    /// <summary>ISO 8601 timestamp of the transition.</summary>
    public string? Timestamp { get; set; }
}

/// <summary>Output produced by a task.</summary>
public sealed class A2AArtifact
{
    public string ArtifactId { get; set; } = Guid.NewGuid().ToString();
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<A2APart> Parts { get; set; } = new();
    public List<string>? Extensions { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>A unit of work tracked across turns.</summary>
public sealed class A2ATask
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "task";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ContextId { get; set; } = Guid.NewGuid().ToString();
    public A2ATaskStatus Status { get; set; } = new();
    public List<A2AArtifact>? Artifacts { get; set; }
    public List<A2AMessage>? History { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Streaming event announcing a task state transition.</summary>
public sealed class A2ATaskStatusUpdateEvent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "status-update";

    public string TaskId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public A2ATaskStatus Status { get; set; } = new();

    /// <summary>True on the last event of the stream.</summary>
    public bool Final { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Streaming event delivering task output.</summary>
public sealed class A2ATaskArtifactUpdateEvent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "artifact-update";

    public string TaskId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public A2AArtifact Artifact { get; set; } = new();
    public bool? Append { get; set; }
    public bool? LastChunk { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Parameters of <c>message/send</c> and <c>message/stream</c>.</summary>
public sealed class A2AMessageSendParams
{
    public A2AMessage Message { get; set; } = new();
    public A2AMessageSendConfiguration? Configuration { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Per-request options carried by <see cref="A2AMessageSendParams.Configuration"/>.</summary>
public sealed class A2AMessageSendConfiguration
{
    public List<string>? AcceptedOutputModes { get; set; }

    /// <summary>When false the server may return before the task reaches a terminal state.</summary>
    public bool? Blocking { get; set; }

    public int? HistoryLength { get; set; }

    /// <summary>Accepted and ignored: this server does not implement push notifications.</summary>
    public JsonElement? PushNotificationConfig { get; set; }
}

/// <summary>Parameters of <c>tasks/get</c>.</summary>
public sealed class A2ATaskQueryParams
{
    public string Id { get; set; } = string.Empty;
    public int? HistoryLength { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Parameters of <c>tasks/cancel</c> and <c>tasks/resubscribe</c>.</summary>
public sealed class A2ATaskIdParams
{
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
