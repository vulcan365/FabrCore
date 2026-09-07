using System.Net;
using System.Text;
using System.Text.Json;
using FabrCore.Core;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2ATaskLifecycleTests
{
    [TestMethod]
    public async Task ConcurrentTaskLimit_RejectsExcessWork_AndReleasesAfterCompletion()
    {
        var config = Config();
        config["A2A:Tasks:MaxConcurrentTasks"] = "1";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(config, GatedAgent(gate));
        var id = await StartNonBlockingAsync(host);
        var rejected = await host.PostJsonAsync("/a2a/botanical-agent", NonBlockingSend());
        using var denial = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.AreEqual(-32050, denial.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        gate.SetResult();
        await PollAsync(host, id, "TASK_STATE_COMPLETED");
        var accepted = await host.PostJsonAsync("/a2a/botanical-agent", NonBlockingSend());
        using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.IsTrue(body.RootElement.TryGetProperty("result", out _));
    }
    private static Dictionary<string, string?> Config() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:AgentTypes:0"] = "botanical-agent",
    };

    /// <summary>An agent service that will not answer until the test lets it.</summary>
    private static FakeFabrCoreAgentService GatedAgent(TaskCompletionSource gate, string reply = "done")
        => new()
        {
            ReplyFactory = async _ =>
            {
                await gate.Task;
                return new AgentMessage { Message = reply, Kind = MessageKind.Response };
            },
        };

    private static string NonBlockingSend() =>
        """
        {"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{
          "message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]},
          "configuration":{"returnImmediately":true}}}
        """;

    private static async Task<string> StartNonBlockingAsync(FabrCoreA2ATestHost host)
    {
        var response = await host.PostJsonAsync("/a2a/botanical-agent", NonBlockingSend());
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = body.RootElement.GetProperty("result").GetProperty("task");

        Assert.IsFalse(task.TryGetProperty("kind", out _));
        Assert.IsFalse(
            task.GetProperty("status").GetProperty("state").GetString() is "TASK_STATE_COMPLETED" or "TASK_STATE_FAILED",
            "A non-blocking send must return before the agent has answered.");

        return task.GetProperty("id").GetString()!;
    }

    [TestMethod]
    public async Task NonBlockingSend_ReturnsImmediatelyAndTheTaskCompletesLater()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(Config(), GatedAgent(gate, "the answer"));

        var taskId = await StartNonBlockingAsync(host);

        gate.SetResult();
        var completed = await PollAsync(host, taskId, "TASK_STATE_COMPLETED");

        Assert.AreEqual(
            "the answer",
            completed.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task CancelTask_MovesARunningTaskToCanceled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(Config(), GatedAgent(gate));

        var taskId = await StartNonBlockingAsync(host);

        var cancel = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            $$"""{"jsonrpc":"2.0","id":2,"method":"CancelTask","params":{"id":"{{taskId}}"} }""");

        using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "TASK_STATE_CANCELED",
            body.RootElement.GetProperty("result").GetProperty("status").GetProperty("state").GetString());

        gate.TrySetResult();
    }

    [TestMethod]
    public async Task CancelTask_OnTheRestRoute_UsesTheSameLifecycle()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(Config(), GatedAgent(gate));

        var taskId = await StartNonBlockingAsync(host);

        var cancel = await host.Client.PostAsync(
            $"/a2a/botanical-agent/tasks/{taskId}:cancel",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.AreEqual("TASK_STATE_CANCELED", body.RootElement.GetProperty("status").GetProperty("state").GetString());

        gate.TrySetResult();
    }

    [TestMethod]
    public async Task CancelTask_ThatAlreadyFinished_IsReportedAsNotCancelable()
    {
        await using var host = await A2ATestHost.StartAsync(Config());

        var send = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");
        using var sent = JsonDocument.Parse(await send.Content.ReadAsStringAsync());
        var taskId = sent.RootElement.GetProperty("result").GetProperty("task").GetProperty("id").GetString();

        var cancel = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            $$"""{"jsonrpc":"2.0","id":2,"method":"CancelTask","params":{"id":"{{taskId}}"} }""");

        using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.AreEqual(-32002, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task CancelTask_ThatIsUnknown_IsReportedAsNotFound()
    {
        await using var host = await A2ATestHost.StartAsync(Config());

        var cancel = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":2,"method":"CancelTask","params":{"id":"nope"}}""");

        using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.AreEqual(-32001, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task Resubscribe_ReplaysWhatWasMissedAndThenStreamsToCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(Config(), GatedAgent(gate, "late answer"));

        var taskId = await StartNonBlockingAsync(host);

        // Subscribe after the task started: the events already emitted must be replayed, so a
        // late subscriber cannot miss the terminal event.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/a2a/botanical-agent/tasks/{taskId}:subscribe")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        var streamTask = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var response = await streamTask;
        response.EnsureSuccessStatusCode();

        gate.SetResult();

        var events = new List<JsonElement>();
        await using (var stream = await response.Content.ReadAsStreamAsync())
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    events.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
                }
            }
        }

        response.Dispose();

        Assert.IsTrue(events[0].TryGetProperty("task", out _));
        var final = events[^1].GetProperty("statusUpdate");
        Assert.IsFalse(final.TryGetProperty("final", out _));
        Assert.AreEqual("TASK_STATE_COMPLETED", final.GetProperty("status").GetProperty("state").GetString());
        Assert.IsTrue(events.Any(e => e.TryGetProperty("artifactUpdate", out _)));
    }

    [TestMethod]
    public async Task Resubscribe_ToAnUnknownTask_IsReportedAsNotFound()
    {
        await using var host = await A2ATestHost.StartAsync(Config());

        var response = await host.Client.GetAsync("/a2a/botanical-agent/tasks/nope:subscribe");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ExecutionTimeout_FailsTheTaskWithAnExplanation()
    {
        var config = Config();
        config["A2A:Tasks:ExecutionTimeout"] = "00:00:00.200";

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(config, GatedAgent(gate));

        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = body.RootElement.GetProperty("result").GetProperty("task");

        Assert.AreEqual("TASK_STATE_FAILED", task.GetProperty("status").GetProperty("state").GetString());
        StringAssert.Contains(
            task.GetProperty("status").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString(),
            "did not respond");

        gate.TrySetResult();
    }

    private static async Task<JsonElement> PollAsync(FabrCoreA2ATestHost host, string taskId, string expectedState)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var task = await host.GetJsonAsync($"/a2a/botanical-agent/tasks/{taskId}");
            if (task.RootElement.GetProperty("status").GetProperty("state").GetString() == expectedState)
            {
                return task.RootElement.Clone();
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Task {taskId} never reached state '{expectedState}'.");
        return default;
    }
}
