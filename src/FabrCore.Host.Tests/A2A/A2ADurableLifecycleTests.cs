using FabrCore.Core;
using FabrCore.Host.A2A;
using FabrCore.Host.A2A.Protocol;
using FabrCore.Host.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2ADurableLifecycleTests
{
    [TestMethod]
    public async Task FailedAcceptanceDoesNotInvokeAgentOrExhaustCapacity()
    {
        var store = new GatedStore { Reject = true };
        var agent = new FakeFabrCoreAgentService();
        await using var host = await FabrCoreA2ATestHost.StartAsync(new Dictionary<string,string?>
        {
            ["A2A:Enabled"]="true", ["A2A:PublicBaseUrl"]="https://example.test", ["A2A:Authentication:Mode"]="None",
            ["A2A:AgentTypes:0"]="test-agent", ["A2A:Tasks:MaxConcurrentTasks"]="1"
        }, agent, new FakeFabrCoreRegistry().WithAgentType("test-agent", "test", "test"), services => services.AddSingleton<IA2ATaskStore>(store));
        for (var i = 0; i < 2; i++)
        {
            using var response = await host.PostJsonAsync("/a2a/test-agent", """
                {"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"role":"user","messageId":"m1","parts":[{"kind":"text","text":"hi"}]}}}
                """);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(-32603, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.IsFalse(body.RootElement.ToString().Contains("sensitive database detail"));
        }
        Assert.IsEmpty(agent.Sends);
    }

    [TestMethod]
    public async Task DurableAcceptancePrecedesEffectsAndCompletionWaitsForCommit()
    {
        var store = new GatedStore();
        var agent = new FakeFabrCoreAgentService { ReplyFactory = _ =>
        {
            Assert.IsNotNull(store.Saved);
            Assert.AreEqual(A2ATaskStates.Working, store.Saved.Status.State);
            return Task.FromResult(new AgentMessage { Message = "done", Kind = MessageKind.Response });
        }};
        await using var host = await FabrCoreA2ATestHost.StartAsync(new Dictionary<string,string?>
        {
            ["A2A:Enabled"]="true", ["A2A:PublicBaseUrl"]="https://example.test", ["A2A:Authentication:Mode"]="None", ["A2A:AgentTypes:0"]="test-agent"
        }, agent, new FakeFabrCoreRegistry().WithAgentType("test-agent", "test", "test"), services => services.AddSingleton<IA2ATaskStore>(store));
        var responseTask = host.PostJsonAsync("/a2a/test-agent", """
            {"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"role":"user","messageId":"m1","parts":[{"kind":"text","text":"hi"}]}}}
            """);
        try
        {
            await store.TerminalAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(responseTask.IsCompleted, "Blocking requests must wait for the terminal SQL commit.");
            Assert.AreEqual(A2ATaskStates.Working, store.Saved!.Status.State);
            using var read = await host.GetJsonAsync("/a2a/test-agent/tasks/" + store.Saved.Id);
            Assert.AreEqual("TASK_STATE_WORKING", read.RootElement.GetProperty("status").GetProperty("state").GetString());
        }
        finally { store.AllowTerminal.TrySetResult(); }
        using var response = await responseTask;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("TASK_STATE_COMPLETED", body.RootElement.GetProperty("result").GetProperty("task").GetProperty("status").GetProperty("state").GetString());
        Assert.AreEqual(A2ATaskStates.Completed, store.Saved!.Status.State);
    }

    private sealed class GatedStore : IA2ATaskStore, IDurableA2ATaskStore
    {
        public A2ATask? Saved;
        public bool Reject;
        public TaskCompletionSource TerminalAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan HeartbeatInterval => TimeSpan.FromHours(1);
        public ValueTask<A2ATask?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(Saved?.Id == id ? Saved : null);
        public async ValueTask SaveAsync(A2ATask task, CancellationToken ct = default)
        {
            if (Reject) throw new InvalidOperationException("sensitive database detail");
            if (A2ATaskStates.IsTerminal(task.Status.State)) { TerminalAttempt.TrySetResult(); await AllowTerminal.Task.WaitAsync(ct); }
            Saved = JsonSerializer.Deserialize<A2ATask>(JsonSerializer.Serialize(task));
        }
        public ValueTask<bool> RequestCancellationAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> IsCancellationRequestedAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(false);
    }
}
