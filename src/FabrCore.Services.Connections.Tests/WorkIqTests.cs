using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FabrCore.Services.RemoteAgents;

namespace FabrCore.Services.Connections.Tests;

[TestClass]
public sealed class WorkIqTests
{
    [TestMethod]
    public async Task StreamingMaintainsTaskContextAndArtifactUpdates()
    {
        var events = new[] {
            """{"result":{"task":{"id":"task","contextId":"context","status":{"state":"TASK_STATE_WORKING"}}}}""",
            """{"result":{"artifactUpdate":{"taskId":"task","contextId":"context","artifact":{"artifactId":"answer","parts":[{"text":"one"}]}}}}""",
            """{"result":{"artifactUpdate":{"taskId":"task","contextId":"context","append":true,"artifact":{"artifactId":"answer","parts":[{"text":"two"}]}}}}""",
            """{"result":{"statusUpdate":{"taskId":"task","contextId":"context","status":{"state":"TASK_STATE_COMPLETED"}}}}"""
        };
        var handler = new Provider(string.Join("", events.Select(e => "data: " + e + "\n\n")), "text/event-stream");
        using var http = new HttpClient(handler); var updates = new List<WorkIqResult>();
        var result = await new WorkIqClient(http, "https://workiq.example/a2a/").Invoke("SendStreamingMessage", new(), p => { updates.Add(p); return Task.CompletedTask; }, default);
        Assert.AreEqual("onetwo", result.Text); Assert.AreEqual("context", result.Context); Assert.AreEqual("task", result.TaskId);
        Assert.AreEqual("TASK_STATE_COMPLETED", result.State); Assert.AreEqual(4, updates.Count);
        Assert.AreEqual("1.0", handler.Version); Assert.AreEqual("SendStreamingMessage", JsonNode.Parse(handler.Body!)!["method"]!.ToString());
    }
    [TestMethod]
    public async Task TaskStatusUsesDocumentedUnwrappedResult()
    {
        using var http = new HttpClient(new Provider("""{"result":{"id":"task","contextId":"context","status":{"state":"TASK_STATE_INPUT_REQUIRED","message":{"parts":[{"text":"Which mailbox?"}]}}}}""", "application/json"));
        var result = await new WorkIqClient(http, "https://workiq.example/a2a/").Invoke("GetTask", new() { ["id"] = "task" }, _ => Task.CompletedTask, default);
        Assert.AreEqual("Which mailbox?", result.Text); Assert.AreEqual("TASK_STATE_INPUT_REQUIRED", result.State);
    }
    private sealed class Provider(string body, string contentType) : HttpMessageHandler
    {
        public string? Version { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Version = request.Headers.GetValues("A2A-Version").Single(); Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }
}
