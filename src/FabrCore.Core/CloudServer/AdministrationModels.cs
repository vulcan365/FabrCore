using System.Text.Json;

namespace FabrCore.Core.CloudServer;

public static class FabrCoreAdminChannels
{
    public const string Admin = "_admin";
    public static bool IsReserved(string? channel) =>
        string.Equals(channel, Admin, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(channel, "_debug", StringComparison.OrdinalIgnoreCase);
    public static void RejectOrdinary(string? channel)
    {
        if (IsReserved(channel)) throw new UnauthorizedAccessException("Reserved diagnostic channels require the administration API.");
    }
}

public sealed class AdminSessionRequest
{
    public string? SourceThreadId { get; set; }
}

public sealed class AdminTurnRequest
{
    public string TurnId { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AdminConversationSession
{
    public string Id { get; set; } = "";
    public string Actor { get; set; } = "";
    public string AgentHandle { get; set; } = "";
    public string? SourceThreadId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<StoredChatMessage> SourceSnapshot { get; set; } = [];
    public List<AdminConversationTurn> Turns { get; set; } = [];
}

public sealed class AdminConversationTurn
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Response { get; set; }
    public string? Error { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<StoredChatMessage> Transcript { get; set; } = [];
}

public sealed class AdminApiResult
{
    public int StatusCode { get; set; } = 200;
    public JsonElement Body { get; set; }
}

public sealed class AdministrationPage<T>
{
    public List<T> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public string? Revision { get; set; }
    public string? SourceId { get; set; }
    public string DataScope { get; set; } = "cluster";
    public bool Complete { get; set; } = true;
}
