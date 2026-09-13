#pragma warning disable MAAI001
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class BackgroundMemoryTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("cancel")]
    public async Task HarnessBackgroundSpecialistInvokesScopedSaveAndRecall(string outcome)
    {
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ScopeKey.Returns("internal:4:core:research");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.SaveMemoryAsync(Arg.Any<string>(), Arg.Any<MemoryType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call => {
                saved.TrySetResult();
                if (outcome == "failure") throw new InvalidOperationException("injected storage failure");
                if (outcome == "cancel")
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
                return new MemoryEntry { Id = Guid.NewGuid(), Title = "checkpoint" };
            });
        memory.RecallAsync(Arg.Any<string>(), null, Arg.Any<CancellationToken>()).Returns(new MemoryRecallResult());
        var step = 0;
        var childClient = new FakeChatClient(_ => Interlocked.Increment(ref step) switch
        {
            1 => Call("save", "SaveMemory", new() { ["title"] = "checkpoint", ["type"] = "Fact", ["content"] = "The service uses port 8123." }),
            2 => Call("recall", "RecallMemories", new() { ["query"] = "service port" }),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Saved and recalled."))
        });
        using var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(childClient)).BuildServiceProvider();
        var host = Substitute.For<IFabrCoreAgentHost>(); host.GetHandle().Returns("owner:core");
        var proxy = new MemoryTestProxy(services, host);
        IAsyncDisposable? childLifetime = null;
        try
        {
            var tools = MemoryHarnessExtensions.CreateMemoryTools(memory);
            var child = await proxy.CreateAsync(new InternalAgentOptions
            {
                Name = "research", Description = "Retains findings", Instructions = "Save and recall the finding.", Model = "default",
                Tools = tools, ToolRisks = MemoryHarnessExtensions.GetMemoryToolRisks(tools),
                ExecutionPolicy = InternalAgentExecutionPolicy.ConcurrentWithMemory,
                EnableContextCompaction = false, EnableOpenTelemetry = false,
                Timeout = TimeSpan.FromSeconds(outcome == "cancel" ? 1 : 10)
            });
            childLifetime = (IAsyncDisposable)child.Agent;
            var parentStep = 0;
            var parent = new FakeChatClient(_ => Interlocked.Increment(ref parentStep) == 1
                ? Call("delegate", "background_agents_start_task", new() { ["agentName"] = "research", ["input"] = "Remember the service port.", ["description"] = "Save finding" })
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Delegated.")));
            var harness = new FabrCoreHarnessAgent(parent, new FabrCoreHarnessOptions
            {
                BackgroundAgents = [child.AsBackgroundAgent()], DisableTodoProvider = true,
                DisableAgentModeProvider = true, DisableOpenTelemetry = true
            });
            var session = await harness.CreateSessionAsync();
            await harness.RunAsync("Delegate the finding.", session);
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (harness.BackgroundAgents!.GetIncompleteTasks(session).Count > 0)
                await Task.Delay(10, timeout.Token);
            if (outcome == "cancel")
            {
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await memory.DidNotReceiveWithAnyArgs().RecallAsync(default!);
            }
            else
            {
                await memory.Received(1).RecallAsync("service port", null, Arg.Any<CancellationToken>());
                Assert.AreEqual(3, childClient.CallCount);
                if (outcome == "failure")
                    Assert.IsTrue(childClient.ReceivedMessages.SelectMany(m => m).SelectMany(m => m.Contents)
                        .OfType<FunctionResultContent>().Any(r => r.Result?.ToString()?.Contains("Error saving memory") == true));
            }
        }
        finally { if (childLifetime is not null) await childLifetime.DisposeAsync(); }
    }

    private static ChatResponse Call(string id, string name, Dictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]));

    private sealed class MemoryTestProxy(IServiceProvider services, IFabrCoreAgentHost host)
        : FabrCoreAgentProxy(new AgentConfiguration { Handle = "owner:core", AgentType = "test" }, services, host)
    {
        public Task<InternalAgentResult> CreateAsync(InternalAgentOptions options) => CreateInternalAgentAsync(options);
        public override Task OnInitialize() => Task.CompletedTask;
        public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
    }
}
