using FabrCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotUserAssertionSinkTests
{
    private static readonly CopilotAgent Agent = CopilotTestAgents.Catalog(CopilotTestAgents.Single()).Agents[0];

    private sealed class Recorder : IConnectionUserAssertions
    {
        public List<(string Principal, string Agent, string Assertion)> Submitted { get; } = [];

        public Task SubmitAsync(string principal, string agent, string assertion, CancellationToken cancellationToken = default)
        {
            Submitted.Add((principal, agent, assertion));
            return Task.CompletedTask;
        }
    }

    private static (ConnectionsCopilotUserAssertionSink Sink, Recorder Recorder) Create()
    {
        var recorder = new Recorder();
        var provider = new ServiceCollection().AddSingleton<IConnectionUserAssertions>(recorder).BuildServiceProvider();
        return (new ConnectionsCopilotUserAssertionSink(provider), recorder);
    }

    [TestMethod]
    public async Task ATokenGoesToThatPersonsOwnAgent()
    {
        var (sink, recorder) = Create();

        await sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None);

        var submitted = recorder.Submitted.Single();
        Assert.AreEqual("alice", submitted.Principal);
        Assert.AreEqual($"alice:{Agent.Handle}", submitted.Agent);
        Assert.AreEqual("token-1", submitted.Assertion);
    }

    [TestMethod]
    public async Task TheSameTokenEachTurnGoesOnce_AndANewOneGoesAgain()
    {
        var (sink, recorder) = Create();

        await sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None);
        await sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None);
        Assert.HasCount(1, recorder.Submitted);

        await sink.SubmitAsync(Agent, "alice", "token-2", CancellationToken.None);
        Assert.HasCount(2, recorder.Submitted);

        await sink.SubmitAsync(Agent, "bob", "token-1", CancellationToken.None);
        Assert.HasCount(3, recorder.Submitted);
    }

    [TestMethod]
    public async Task AFailedSubmissionIsTriedAgainNextTurn()
    {
        var failing = new FailingOnce();
        var sink = new ConnectionsCopilotUserAssertionSink(new ServiceCollection().AddSingleton<IConnectionUserAssertions>(failing).BuildServiceProvider());

        await Assert.ThrowsExactlyAsync<ConnectionException>(() => sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None));
        await sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None);

        Assert.AreEqual(2, failing.Calls);
    }

    [TestMethod]
    public async Task AHostWithoutConnectionsDoesNothing()
    {
        var sink = new ConnectionsCopilotUserAssertionSink(new ServiceCollection().BuildServiceProvider());

        await sink.SubmitAsync(Agent, "alice", "token-1", CancellationToken.None);
    }

    private sealed class FailingOnce : IConnectionUserAssertions
    {
        public int Calls { get; private set; }

        public Task SubmitAsync(string principal, string agent, string assertion, CancellationToken cancellationToken = default)
            => ++Calls == 1 ? throw new ConnectionException("provider-unavailable", "down") : Task.CompletedTask;
    }
}
