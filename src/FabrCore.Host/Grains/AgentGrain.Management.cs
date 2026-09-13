using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using Orleans;

namespace FabrCore.Host.Grains;

internal partial class AgentGrain
{
    private bool managementBusy;
    public async Task<string> ManageAsync(string operation, string body)
    {
        var request = JsonSerializer.Deserialize<AgentManagementRequest>(body, AdminJson) ?? new();
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { configuration = _messageState.State.Configuration, state = _messageState.State.CustomState, threads = _messageState.State.MessageThreads })));
        if (operation == "get") return AdminResult(200, new AgentManagementSnapshot
        {
            AgentHandle = this.GetPrimaryKeyString(), Revision = revision, Configuration = Clone(agentConfiguration),
            State = Clone(_messageState.State.CustomState), Threads = _messageState.State.MessageThreads.Keys.Order().ToList(),
            Processing = fabrcoreAgentProxy?.InternalIsProcessingMessage == true, AdminProcessing = adminTurnBusy
        });
        if (operation == "thread")
        {
            if (request.Revision is not null && request.Revision != revision) return AdminResult(412, new { error = "Thread changed; restart paging." });
            var messages = Clone(await GetThreadMessages(request.ThreadId ?? ""));
            var offset = Math.Max(0, request.Offset); var limit = Math.Clamp(request.Limit, 1, 1000);
            return AdminResult(200, new AdministrationPage<StoredChatMessage> { Items = messages.Skip(offset).Take(limit).ToList(),
                Revision = revision, NextCursor = offset + limit < messages.Count ? (offset + limit).ToString() : null });
        }
        if (adminTurnBusy || adminControlBusy || fabrcoreAgentProxy?.InternalIsProcessingMessage == true)
            return AdminResult(409, new { error = "Agent is actively processing." });
        if (request.Revision != revision) return AdminResult(412, new { error = "Agent changed; refresh and review the operation.", revision });
        if (managementBusy) return AdminResult(409, new { error = "Agent management is busy." });
        managementBusy = true;
        try
        {
        switch (operation)
        {
            case "configure":
                if (request.Configuration is null) return AdminResult(400, new { error = "Configuration required." });
                request.Configuration.Handle = HandleUtilities.ParseHandle(this.GetPrimaryKeyString()).AgentHandle;
                return AdminResult(200, await ConfigureAgent(request.Configuration, true));
            case "reset": return AdminResult(200, await ResetAgent());
            case "restart":
                if (agentConfiguration is null) return AdminResult(409, new { error = "Agent is not configured." });
                return AdminResult(200, await ConfigureAgent(Clone(agentConfiguration), true));
            case "state": await MergeCustomStateAsync(request.Changes, request.Deletes); break;
            case "append-thread": await AddThreadMessages(request.ThreadId ?? "", request.Messages); break;
            case "replace-thread": await ReplaceThreadMessages(request.ThreadId ?? "", request.Messages); break;
            case "clear-thread": await ClearThreadMessages(request.ThreadId ?? ""); break;
            default: return AdminResult(400, new { error = "Unknown management action." });
        }
        return AdminResult(200, new { completed = true });
        }
        finally { managementBusy = false; }
    }
}
