using System.Text.Json;

namespace FabrCore.Core.CloudServer;

public sealed class AgentManagementRequest
{
    public string? Revision { get; set; }
    public AgentConfiguration? Configuration { get; set; }
    public string? ThreadId { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public List<StoredChatMessage> Messages { get; set; } = [];
    public Dictionary<string, JsonElement> Changes { get; set; } = [];
    public List<string> Deletes { get; set; } = [];
}

public sealed class AgentManagementSnapshot
{
    public string AgentHandle { get; set; } = "";
    public string Revision { get; set; } = "";
    public AgentConfiguration? Configuration { get; set; }
    public Dictionary<string, JsonElement> State { get; set; } = [];
    public List<string> Threads { get; set; } = [];
    public bool Processing { get; set; }
    public bool AdminProcessing { get; set; }
}
