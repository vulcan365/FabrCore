using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.RemoteAgents;

public sealed class RemoteAgentOptions
{
    public bool Enabled { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}

public static class RemoteAgentExtensions
{
    public static IServiceCollection AddFabrCoreRemoteAgents(this IServiceCollection services, Action<RemoteAgentOptions> configure)
    {
        var options = new RemoteAgentOptions(); configure(options);
        if (options.Enabled)
        {
            if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(options.Timeout));
            services.AddSingleton(options);
        }
        return services;
    }
}

/// <summary>Opt-in remote conversation proxy. Register its assembly with the FabrCore agent registry.</summary>
[AgentAlias("remote-agent")]
public sealed class RemoteAgent(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
    : FabrCoreAgentProxy(config, services, host)
{
    public override Task OnInitialize() => Task.CompletedTask;
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var options = serviceProvider.GetService<RemoteAgentOptions>();
        if (options?.Enabled != true) { response.MessageType = "_error"; response.Message = "Remote agents are disabled."; return response; }
        // Delegated remote conversations cannot be used as cross-principal credential proxies.
        var caller = HandleUtilities.ParseHandle(message.FromHandle ?? "").UserHandle;
        var owner = fabrcoreAgentHost.GetUserHandle();
        if (caller != owner && message.FromHandle != owner)
        { response.MessageType = "_error"; response.Message = "Remote conversations are restricted to their owning principal."; return response; }
        using var timeout = new CancellationTokenSource(options.Timeout);
        try
        {
            using var http = await Connections.GetHttpClientAsync(Required("connection"), Required("resource"), timeout.Token);
            var provider = Required("provider");
            var endpoint = Required("endpoint");
            // Bind persisted context to the provider, endpoint, and connection configuration.
            var identity = provider + "|" + endpoint + "|" + Required("connection") + "|" + Required("resource") + "|" + await Connections.GetSessionVersionAsync(Required("connection"), timeout.Token);
            var previous = await GetStateAsync<string>("remote-binding");
            var context = previous == identity ? await GetStateAsync<string>("remote-context") : null;
            var taskId = previous == identity ? await GetStateAsync<string>("remote-task") : null;
            var taskState = previous == identity ? await GetStateAsync<string>("remote-task-state") : null;
            if (message.MessageType == "_remote-reset")
            {
                SetState<string?>("remote-context", null); SetState<string?>("remote-task", null); SetState<string?>("remote-task-state", null);
                SetState("remote-binding", identity); await FlushStateAsync();
                response.Message = "Remote conversation reset."; return response;
            }
            if (provider == "work-iq")
            {
                var method = message.MessageType switch { "_remote-task-status" => "GetTask", "_remote-task-cancel" => "CancelTask", "_remote-task-resume" => "SubscribeToTask", _ => config.Args.GetValueOrDefault("streaming") == "false" ? "SendMessage" : "SendStreamingMessage" };
                JsonObject parameters;
                if (method is "GetTask" or "CancelTask" or "SubscribeToTask")
                    parameters = new() { ["id"] = taskId ?? throw new ConnectionException("remote-error", "No remote task is associated with this conversation.") };
                else
                {
                    if (taskState is "TASK_STATE_WORKING" or "TASK_STATE_SUBMITTED")
                        throw new ConnectionException("remote-task-active", "Inspect, resume, or cancel the active task before sending another message.");
                    var zone = config.Args.GetValueOrDefault("timeZone", "Etc/UTC");
                    var outgoing = new JsonObject {
                        ["role"] = "ROLE_USER", ["messageId"] = Guid.NewGuid().ToString(), ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Message ?? "" }),
                        ["metadata"] = new JsonObject { ["Location"] = new JsonObject { ["timeZone"] = zone, ["timeZoneOffset"] = (int)TimeZoneInfo.FindSystemTimeZoneById(zone).GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes } }
                    };
                    if (context is not null) outgoing["contextId"] = context;
                    if (taskState is "TASK_STATE_INPUT_REQUIRED" or "TASK_STATE_AUTH_REQUIRED") outgoing["taskId"] = taskId;
                    else { taskId = null; taskState = null; }
                    parameters = new() { ["message"] = outgoing };
                }
                var lastText = "";
                var result = await new WorkIqClient(http, endpoint).Invoke(method, parameters, async progress => {
                    context = progress.Context ?? context; taskId = progress.TaskId ?? taskId; taskState = progress.State ?? taskState;
                    SetState("remote-binding", identity); SetState("remote-context", context); SetState("remote-task", taskId); SetState("remote-task-state", taskState);
                    await FlushStateAsync();
                    if (progress.Text.Length > 0 && progress.Text != lastText) { await SendToUserAsync(progress.Text, "_remote-progress"); lastText = progress.Text; }
                }, timeout.Token);
                response.Message = result.Text;
                response.DataType = "application/json";
                response.Data = JsonSerializer.SerializeToUtf8Bytes(result.Payload);
                response.Args = new() { ["remoteTaskId"] = taskId ?? "", ["remoteTaskState"] = taskState ?? "" };
                if (taskState is not (null or "TASK_STATE_COMPLETED")) response.MessageType = "_remote-task";
            }
            else if (provider == "copilot-studio")
            {
                var client = new CopilotClient(new ConnectionSettings { DirectConnectUrl = endpoint }, new FixedFactory(http), logger);
                if (context is null)
                    await foreach (var activity in client.StartConversationAsync(false, timeout.Token)) context = activity.Conversation?.Id ?? context;
                if (context is null) throw new ConnectionException("remote-error", "Copilot Studio did not establish a conversation.");
                SetState("remote-binding", identity); SetState("remote-context", context); await FlushStateAsync();
                var texts = new List<string>();
                var activities = new List<JsonElement>();
                await foreach (var activity in client.AskQuestionAsync(message.Message ?? "", context, timeout.Token))
                {
                    context = activity.Conversation?.Id ?? context;
                    if (activity.Type == "message")
                    {
                        if (!string.IsNullOrEmpty(activity.Text)) texts.Add(activity.Text);
                        activities.Add(JsonSerializer.SerializeToElement(activity, activity.GetType()));
                    }
                    else if (activity.Type == "typing") await SendToUserAsync("Copilot is responding", "_status");
                }
                response.Message = string.Join("\n", texts);
                response.DataType = "application/json"; response.Data = JsonSerializer.SerializeToUtf8Bytes(activities);
            }
            else throw new ConnectionException("invalid-configuration", "Unknown remote agent provider.");
            SetState("remote-binding", identity);
            SetState("remote-context", context);
            await FlushStateAsync();
        }
        catch (ConnectionException e)
        {
            response.MessageType = e.Code == "interaction-required" ? "_connection-required" : "_error";
            response.Message = e.Message;
            response.Args = new() { ["connection"] = config.Args.GetValueOrDefault("connection", ""), ["error"] = e.Code };
        }
        catch (OperationCanceledException) { response.MessageType = "_error"; response.Message = "Remote request timed out; execution may have completed. The request was not retried."; }
        catch (HttpRequestException) { response.MessageType = "_error"; response.Message = "The remote service rejected the request or could not be reached."; }
        catch (JsonException) { response.MessageType = "_error"; response.Message = "The remote service returned an invalid protocol response."; }
        catch (TimeZoneNotFoundException) { response.MessageType = "_error"; response.Message = "The configured time zone is invalid."; }
        return response;
    }

    private string Required(string name) => config.Args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ConnectionException("invalid-configuration", $"Remote agent requires '{name}'.");

    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Forwarder(client)) { BaseAddress = client.BaseAddress };
    }
    // SDK-created clients can be disposed independently; the authenticated transport lives for the turn.
    private sealed class Forwarder(HttpClient client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) {
                Content = request.Content, Version = request.Version, VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }
}
