using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FabrCore.Connections;

namespace FabrCore.Services.RemoteAgents;

internal sealed record WorkIqResult(string Text, string? Context, string? TaskId, string? State, JsonNode Payload);

internal sealed class WorkIqClient(HttpClient http, string endpoint)
{
    internal async Task<WorkIqResult> Invoke(string method, JsonObject parameters, Func<WorkIqResult, Task> progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = JsonContent.Create(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString(), ["method"] = method, ["params"] = parameters })
        };
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
            var payload = await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new ConnectionException("remote-error", "Empty A2A response.");
            var result = Read(payload); await progress(result); return result;
        }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var data = new StringBuilder(); var total = 0;
        var task = new JsonObject { ["artifacts"] = new JsonArray() };
        WorkIqResult? latest = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            total += line.Length;
            if (total > 2 * 1024 * 1024) throw new ConnectionException("remote-error", "The remote stream exceeded its response limit.");
            if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart(' '));
            if (line.Length != 0 || data.Length == 0) continue;
            var text = data.ToString().Trim(); data.Clear();
            if (text == "[DONE]") break;
            var payload = JsonNode.Parse(text) ?? throw new ConnectionException("remote-error", "Empty A2A event.");
            if (payload["error"] is not null) throw new ConnectionException("remote-error", "Work IQ returned a protocol error.");
            var result = payload["result"];
            if (result?["task"] is JsonObject snapshot) task = (JsonObject)snapshot.DeepClone();
            else if (result?["message"] is not null) { latest = Read(payload); await progress(latest); continue; }
            else if (result?["statusUpdate"] is JsonObject status)
            {
                task["id"] = status["taskId"]?.DeepClone() ?? task["id"]?.DeepClone();
                task["contextId"] = status["contextId"]?.DeepClone() ?? task["contextId"]?.DeepClone();
                task["status"] = status["status"]?.DeepClone();
            }
            else if (result?["artifactUpdate"] is JsonObject update)
            {
                task["id"] = update["taskId"]?.DeepClone() ?? task["id"]?.DeepClone();
                task["contextId"] = update["contextId"]?.DeepClone() ?? task["contextId"]?.DeepClone();
                var artifact = update["artifact"]?.DeepClone();
                if (artifact is not null)
                {
                    if (task["artifacts"] is not JsonArray) task["artifacts"] = new JsonArray();
                    var artifacts = task["artifacts"]!.AsArray();
                    var existing = artifacts.FirstOrDefault(a => a?["artifactId"]?.ToString() == artifact["artifactId"]?.ToString());
                    if (existing is not null && update["append"]?.GetValue<bool>() == true)
                    {
                        if (existing["parts"] is not JsonArray) existing["parts"] = new JsonArray();
                        foreach (var part in artifact["parts"]?.AsArray() ?? []) existing["parts"]!.AsArray().Add(part?.DeepClone());
                    }
                    else { if (existing is not null) artifacts.Remove(existing); artifacts.Add(artifact); }
                }
            }
            latest = Read(new JsonObject { ["result"] = new JsonObject { ["task"] = task.DeepClone() } });
            await progress(latest);
        }
        return latest ?? throw new ConnectionException("remote-error", "The remote stream ended without a result.");
    }
    internal static WorkIqResult Read(JsonNode payload)
    {
        if (payload["error"] is not null) throw new ConnectionException("remote-error", "Work IQ returned a protocol error.");
        var result = payload["result"];
        var task = result?["task"] ?? (result?["status"] is not null ? result : null);
        var artifacts = task?["artifacts"]?.AsArray().Select(a => string.Concat((a?["parts"]?.AsArray() ?? []).Select(p => p?["text"]?.GetValue<string>()))) ?? [];
        var parts = result?["message"]?["parts"]?.AsArray() ?? task?["status"]?["message"]?["parts"]?.AsArray();
        var texts = artifacts.Concat((parts ?? []).Select(p => p?["text"]?.GetValue<string>())).Where(t => !string.IsNullOrEmpty(t));
        return new(string.Join("\n", texts), task?["contextId"]?.GetValue<string>() ?? result?["message"]?["contextId"]?.GetValue<string>(),
            task?["id"]?.GetValue<string>(), task?["status"]?["state"]?.GetValue<string>(), payload);
    }
}
