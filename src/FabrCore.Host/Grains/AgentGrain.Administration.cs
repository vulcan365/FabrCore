using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Host.Grains;

internal partial class AgentGrain
{
    private bool adminControlBusy;
    private bool adminTurnBusy;
    private int lifecycleDepth;
    private readonly string adminActivationId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web);
    private void RejectAdminBusy()
    {
        if (adminTurnBusy || adminControlBusy || fabrcoreAgentProxy?.InternalIsProcessingAdmin == true)
            throw new InvalidOperationException("Agent is actively processing an admin operation.");
    }

    public async Task<string> AdministerAsync(string actor, string operation, string? sessionId, string body)
    {
        if (string.IsNullOrWhiteSpace(actor)) return AdminResult(403, new { error = "An authenticated actor is required." });
        if (_isEvicting || managementBusy || lifecycleDepth != 0) return AdminResult(409, new { error = "Agent lifecycle management is in progress." });
        if (adminControlBusy) return AdminResult(409, new { error = "An administration request is busy." });
        adminControlBusy = true;
        try
        {
            var storage = serviceProvider.GetRequiredService<IUserScopedFabrCoreStorageProvider>();
            var principal = HandleUtilities.ParseHandle(this.GetPrimaryKeyString()).UserHandle;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.GetPrimaryKeyString() + "\n" + actor)));
            const string container = "fabrcore.admin-sessions";
            var sessions = await storage.GetAsync<List<AdminConversationSession>>(principal, container, key + ".index") ?? [];
            if (operation == "list") return AdminResult(200, sessions.Select(s => new { s.Id, s.SourceThreadId, s.CreatedAt, s.AgentHandle }));
            if (operation == "snapshot") return AdminResult(200, await ReadAdminDataAsync("snapshot", null));
            if (operation == "create")
            {
                if (adminTurnBusy) return AdminResult(409, new { error = "An admin turn is running." });
                if (sessions.Count >= 100) return AdminResult(409, new { error = "Delete an old admin session before creating another (limit 100 per actor/agent)." });
                var request = JsonSerializer.Deserialize<AdminSessionRequest>(body, AdminJson) ?? new();
                if (request.SourceThreadId is not null && !_messageState.State.MessageThreads.ContainsKey(request.SourceThreadId) && !_activeChatHistoryProviders.Any(p => p.ThreadId == request.SourceThreadId))
                    return AdminResult(404, new { error = "Source thread not found." });
                var snapshot = request.SourceThreadId is null ? [] : CaptureAdminThread(request.SourceThreadId);
                if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 1024 * 1024)
                    return AdminResult(413, new { error = "Source history exceeds the 1 MiB fork limit; use an empty fork and bounded read tools." });
                var created = new AdminConversationSession { Id = Guid.NewGuid().ToString("N"), Actor = actor,
                    AgentHandle = this.GetPrimaryKeyString(), SourceThreadId = request.SourceThreadId,
                    CreatedAt = DateTimeOffset.UtcNow, SourceSnapshot = snapshot };
                await storage.UpsertAsync(principal, container, key + "." + created.Id, created);
                sessions.Add(new() { Id = created.Id, Actor = actor, AgentHandle = created.AgentHandle, CreatedAt = created.CreatedAt, SourceThreadId = created.SourceThreadId });
                await storage.UpsertAsync(principal, container, key + ".index", sessions);
                return AdminResult(201, sessions[^1]);
            }
            var session = sessionId is null ? null : await storage.GetAsync<AdminConversationSession>(principal, container, key + "." + sessionId);
            if (session?.Actor != actor) session = null;
            if (session is null) return AdminResult(404, new { error = "Admin session not found." });
            if (!adminTurnBusy)
                foreach (var pending in session.Turns.Where(t => t.Status == "running"))
                { pending.Status = "incomplete"; pending.Error = "Execution was interrupted; submit a new turn explicitly."; }
            if (operation == "get") return AdminResult(200, new { session.Id, session.Actor, session.AgentHandle, session.CreatedAt, session.SourceThreadId,
                turns = session.Turns.Select(t => new { t.Id, t.Message, t.Status, t.Response, t.Error, t.TraceId, t.StartedAt, t.CompletedAt }),
                sourceMessageCount = session.SourceSnapshot.Count });
            if (operation == "turn-get")
            {
                var id = JsonSerializer.Deserialize<string>(body);
                var found = session.Turns.SingleOrDefault(t => t.Id == id);
                return found is null ? AdminResult(404, new { error = "Turn not found." }) : AdminResult(200, found);
            }
            if (operation == "delete")
            {
                if (adminTurnBusy) return AdminResult(409, new { error = "An admin turn is running." });
                await storage.DeleteAsync(principal, container, key + "." + session.Id);
                sessions.RemoveAll(s => s.Id == session.Id); await storage.UpsertAsync(principal, container, key + ".index", sessions);
                return AdminResult(200, new { deleted = true });
            }
            if (operation != "turn") return AdminResult(400, new { error = "Unknown operation." });
            var input = JsonSerializer.Deserialize<AdminTurnRequest>(body, AdminJson) ?? new();
            if (string.IsNullOrWhiteSpace(input.TurnId) || input.TurnId.Length > 128 || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 16000)
                return AdminResult(400, new { error = "A turn ID (1–128 characters) and message (1–16000 characters) are required." });
            var existing = session.Turns.SingleOrDefault(t => t.Id == input.TurnId);
            if (existing is not null) return existing.Message == input.Message ? AdminResult(200, existing) : AdminResult(409, new { error = "Turn ID already belongs to another message." });
            if (adminTurnBusy) return AdminResult(409, new { error = "An admin turn is already running for this agent." });
            if (session.Turns.Count >= 100) return AdminResult(409, new { error = "Start a new diagnostic session (100 turn limit)." });
            if (fabrcoreAgentProxy is null) return AdminResult(409, new { error = "Agent is not configured." });
            var turn = new AdminConversationTurn { Id = input.TurnId, Message = input.Message, Status = "running",
                StartedAt = DateTimeOffset.UtcNow, TraceId = ActivityTraceId.CreateRandom().ToString() };
            session.Turns.Add(turn);
            await storage.UpsertAsync(principal, container, key + "." + session.Id, session);
            adminTurnBusy = true;
            DelayDeactivation(TimeSpan.FromMinutes(11));
            _ = CompleteAdminTurnAsync(storage, principal, container, key + "." + session.Id, session, turn);
            return AdminResult(202, turn);
        }
        catch (JsonException) { return AdminResult(400, new { error = "Invalid JSON request." }); }
        finally { adminControlBusy = false; }
    }

    private async Task CompleteAdminTurnAsync(IUserScopedFabrCoreStorageProvider storage, string principal, string container,
        string key, AdminConversationSession session, AdminConversationTurn turn)
    {
        try
        {
            await fabrcoreAgentProxy!.InternalOnAdminMessage(new AdminDiagnosticContext { Session = session, Turn = turn, ReadAsync = ReadAdminDataAsync }, CancellationToken.None);
            turn.Status = "completed";
        }
        catch (Exception ex)
        {
            turn.Status = "incomplete"; turn.Error = ex is OperationCanceledException or TimeoutException ? "Admin turn timed out or was cancelled." :
                ex is AdminModelUnavailableException ? ex.Message : "Diagnostic execution failed; inspect host diagnostics.";
            logger.LogWarning(ex, "Admin diagnostic turn {TurnId} failed", turn.Id);
        }
        finally
        {
            turn.CompletedAt = DateTimeOffset.UtcNow;
            try { await storage.UpsertAsync(principal, container, key, session); }
            catch (Exception ex) { logger.LogError(ex, "Unable to persist admin turn {TurnId}; its running receipt must not be replayed", turn.Id); }
            adminTurnBusy = false;
        }
    }

    private async Task<string> ReadAdminDataAsync(string kind, string? id)
    {
        object data;
        switch (kind)
        {
            case "snapshot":
                data = new { observedAt = DateTimeOffset.UtcNow, scope = "persisted-state", activation = adminActivationId,
                    agent = this.GetPrimaryKeyString(), configuration = Clone(agentConfiguration), state = Clone(_messageState.State.CustomState),
                    threads = _messageState.State.MessageThreads.Select(t => new { id = t.Key, count = t.Value.Count }).ToList(),
                    processing = fabrcoreAgentProxy?.InternalIsProcessingMessage, adminProcessing = adminTurnBusy };
                break;
            case "thread": data = new { threadId = id, observedAt = DateTimeOffset.UtcNow,
                    scope = "supported-history-snapshot", messages = id is null ? [] : CaptureAdminThread(id).TakeLast(100).ToList(), limit = 100 }; break;
            case "errors": data = (await _messageMonitor.GetLlmCallsAsync(this.GetPrimaryKeyString(), 1000))
                    .Where(c => c.ErrorMessage is not null || c.ToolCalls?.Count > 0 || c.RequestMessages?.Any(m => m.Role == "tool") == true).Take(100).ToList(); break;
            case "evidence":
                var bundle = await serviceProvider.GetRequiredService<IVerifiableExecutionStore>().GetBundleAsync(id ?? "");
                data = bundle.Records.Any(r => r.AgentHandle == this.GetPrimaryKeyString()) ? (object)bundle : new { error = "No evidence for this target." }; break;
            default: throw new ArgumentException("Unknown diagnostic query.");
        }
        var json = JsonSerializer.Serialize(data, AdminJson);
        return json.Length <= 128000 ? json : JsonSerializer.Serialize(new { error = "Diagnostic data exceeds the 128000 character tool limit; inspect through paged management APIs." });
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private List<StoredChatMessage> CaptureAdminThread(string threadId) =>
        _activeChatHistoryProviders.LastOrDefault(p => p.ThreadId == threadId)?.CaptureDiagnosticSnapshot()
        ?? Clone(_messageState.State.MessageThreads.GetValueOrDefault(threadId) ?? []);
    private static string AdminResult(int status, object value) => JsonSerializer.Serialize(new AdminApiResult
        { StatusCode = status, Body = JsonSerializer.SerializeToElement(value, AdminJson) }, AdminJson);
}
