using System.Reflection;
using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.Testing;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class FabrCoreCopilotAgentStreamingTests
{
    private const string ErrorResponse = "Expected error response";
    private static readonly TimeSpan AgentDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SaveStateDelay = TimeSpan.FromMilliseconds(75);

    [TestMethod]
    public async Task StreamingSuccess_UsesOnlyStreamingTypingActivities()
    {
        var (adapter, agent) = CreateAgent(async () =>
        {
            await Task.Delay(AgentDelay);
            return new AgentMessage { Message = "Finished response" };
        });

        await adapter.SendTextToBotAsync("hello", agent.OnTurnAsync, CancellationToken.None);

        AssertStreamingLifecycle(adapter, "Finished response");
    }

    [TestMethod]
    public async Task StreamingError_UsesOnlyStreamingTypingActivities()
    {
        var (adapter, agent) = CreateAgent(async () =>
        {
            await Task.Delay(AgentDelay);
            throw new InvalidOperationException("Agent failed");
        });

        await adapter.SendTextToBotAsync("hello", agent.OnTurnAsync, CancellationToken.None);

        AssertStreamingLifecycle(adapter, ErrorResponse);
    }

    private static (TestAdapter Adapter, FabrCoreCopilotAgent Agent) CreateAgent(
        Func<Task<AgentMessage>> replyFactory)
    {
        var adapter = new TestAdapter(
            "msteams:copilot",
            sendTraceActivity: false,
            NullLogger<TestAdapter>.Instance,
            tokenClient: null);

        var appOptions = new AgentApplicationOptions(new MemoryStorage(), NullLoggerFactory.Instance)
        {
            StartTypingTimer = true,
            TypingOptions = new TypingOptions
            {
                InitialDelayMs = 100,
                IntervalMs = 25,
            },
            TurnStateFactory = () => DelayedTurnStateProxy.Create(SaveStateDelay),
        };

        var copilotOptions = new Microsoft365CopilotOptions
        {
            WelcomeMessage = null,
            ErrorMessage = ErrorResponse,
            Streaming = new CopilotStreamingOptions
            {
                Enabled = true,
                InformativeUpdate = "Working on it...",
            },
        };

        var agent = new FabrCoreCopilotAgent(
            appOptions,
            AgentServiceProxy.Create(replyFactory),
            new StubPrincipalResolver(),
            new StubProvisioner(),
            ThrowingProxy.Create<IGrainFactory>(),
            Options.Create(copilotOptions),
            NullLogger<FabrCoreCopilotAgent>.Instance);

        return (adapter, agent);
    }

    private static void AssertStreamingLifecycle(TestAdapter adapter, string expectedText)
    {
        var activities = adapter.GetActivitySnapshot();
        var finalIndex = Array.FindLastIndex(
            activities,
            activity => activity.Type == ActivityTypes.Message && activity.Text == expectedText);

        Assert.IsGreaterThanOrEqualTo(0, finalIndex, "The final streamed message was not sent.");
        Assert.IsTrue(
            activities.Take(finalIndex).Any(activity =>
                activity.Type == ActivityTypes.Typing && activity.GetStreamingEntity() is not null),
            "The streaming progress indicator was not sent before stream completion.");
        Assert.IsFalse(
            activities.Any(activity =>
                activity.Type == ActivityTypes.Typing && activity.GetStreamingEntity() is null),
            "An ordinary typing activity was emitted during a streaming turn.");
        Assert.AreEqual(
            activities.Length - 1,
            finalIndex,
            "The final streamed message must be the last outbound activity.");
    }

    private sealed class StubPrincipalResolver : ICopilotPrincipalResolver
    {
        public ValueTask<string?> ResolvePrincipalHandleAsync(
            ITurnContext turnContext,
            string? userAccessToken,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("test-user");
    }

    private sealed class StubProvisioner : ICopilotAgentProvisioner
    {
        public Task<string> EnsureAgentAsync(
            string principalHandle,
            string? conversationId,
            CancellationToken cancellationToken)
            => Task.FromResult("copilot");

        public void Invalidate(string principalHandle, string agentHandle)
        {
        }
    }

    private class AgentServiceProxy : DispatchProxy
    {
        private Func<Task<AgentMessage>> _replyFactory = null!;

        public static IFabrCoreAgentService Create(Func<Task<AgentMessage>> replyFactory)
        {
            var service = DispatchProxy.Create<IFabrCoreAgentService, AgentServiceProxy>();
            ((AgentServiceProxy)(object)service)._replyFactory = replyFactory;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IFabrCoreAgentService.SendAndReceiveMessageAsync)
                && args is { Length: 3 }
                && args[2] is AgentMessage)
            {
                return _replyFactory();
            }

            throw new NotSupportedException($"Unexpected agent service call: {targetMethod?.Name}");
        }
    }

    private class DelayedTurnStateProxy : DispatchProxy
    {
        private readonly TurnState _inner = new();
        private TimeSpan _saveDelay;

        public static ITurnState Create(TimeSpan saveDelay)
        {
            var state = DispatchProxy.Create<ITurnState, DelayedTurnStateProxy>();
            ((DelayedTurnStateProxy)(object)state)._saveDelay = saveDelay;
            return state;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(ITurnState.SaveStateAsync))
            {
                return Task.Delay(_saveDelay, (CancellationToken)args![2]!);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

    private class ThrowingProxy : DispatchProxy
    {
        public static T Create<T>() where T : class
            => DispatchProxy.Create<T, ThrowingProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException($"Unexpected call: {targetMethod?.Name}");
    }
}
