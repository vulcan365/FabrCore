using FabrCore.Core;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Components;
using FabrCore.Surface.Identity;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FabrCore.Surface.Tests;

public sealed class SurfaceChatWorkspaceTests
{
    private const string Principal = "admin-1";

    [Fact]
    public async Task SendChat_LetsTheCallerChangeTheMessage_BeforeItGoes()
    {
        var context = new RecordingContext(Principal);
        var workspace = await StartAsync(context);

        await workspace.SendChatAsync("Hello", "system:shared", fileIds: null, message =>
        {
            message.Channel = "admin-1-shared";
            message.Args ??= [];
            message.Args["who"] = "Pat";
        }, CancellationToken.None);

        var sent = Assert.Single(context.Sent);
        Assert.Equal("system:shared", sent.ToHandle);
        Assert.Equal("admin-1-shared", sent.Channel);
        Assert.Equal("Pat", sent.Args!["who"]);
        Assert.Single(workspace.GetVisibleTimelineForAgent("system:shared"));
    }

    [Fact]
    public async Task ClearTimeline_ForgetsOnlyThatAgentsConversation()
    {
        var context = new RecordingContext(Principal);
        var workspace = await StartAsync(context);
        await workspace.SendChatAsync("To helper", "helper", fileIds: null, configure: null, CancellationToken.None);
        await workspace.SendChatAsync("To other", "other", fileIds: null, configure: null, CancellationToken.None);
        context.Raise(new AgentMessage { Id = "answer-1", FromHandle = "admin-1:helper", Message = "Answer" });

        workspace.ClearTimeline("helper");

        Assert.Empty(workspace.GetTimelineForAgent("admin-1:helper"));
        Assert.Single(workspace.GetTimelineForAgent("admin-1:other"));

        // The same delivery again is a new message once its conversation is cleared.
        context.Raise(new AgentMessage { Id = "answer-1", FromHandle = "admin-1:helper", Message = "Answer" });
        Assert.Single(workspace.GetTimelineForAgent("admin-1:helper"));
    }

    [Fact]
    public void Problems_AreSaidByWhichStepFailed()
    {
        var problem = SurfaceChatProblem.From(
            new SurfaceConnectionException(SurfaceConnectionProblem.AgentMissing, "Not on the host."), "Helper");

        Assert.Equal("Helper isn't set up yet", problem.Title);
        Assert.Equal("Not on the host.", problem.Message);
        Assert.Equal(SurfaceConnectionProblem.AgentMissing, problem.Problem);
    }

    private static async Task<SurfaceWorkspaceService> StartAsync(RecordingContext context)
    {
        var workspace = new SurfaceWorkspaceService(
            Options.Create(new SurfaceOptions { EnableSharedAgents = false }),
            NullLogger<SurfaceWorkspaceService>.Instance,
            new SingleContextFactory(context));
        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext(Principal, "Pat", true, "test"));
        return workspace;
    }

    private sealed class SingleContextFactory(ISurfacePrincipalContext context) : ISurfacePrincipalContextFactory
    {
        public Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default) => Task.FromResult(context);
        public Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default) => Task.FromResult(context);
        public Task<bool> ReleaseAsync(string handle) => Task.FromResult(true);
        public bool HasContext(string handle) => true;
    }

    private sealed class RecordingContext(string handle) : ISurfacePrincipalContext
    {
        public List<AgentMessage> Sent { get; } = [];
        public string Handle { get; } = handle;
        public bool IsDisposed => false;
        public event EventHandler<AgentMessage>? AgentMessageReceived;

        public void Raise(AgentMessage message) => AgentMessageReceived?.Invoke(this, message);

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request) => Task.FromResult(new AgentMessage { FromHandle = request.ToHandle });

        public Task SendMessage(AgentMessage request)
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEvent(EventMessage request) => Task.CompletedTask;
        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration) => Task.FromResult(Healthy(agentConfiguration.Handle!));
        public Task<AgentHealthStatus> ResetAgent(string handle) => Task.FromResult(Healthy(handle));
        public Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => Task.FromResult(Healthy(handle));
        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false) => Task.FromResult(new List<TrackedAgentInfo>());
        public Task<bool> IsAgentTracked(string handle) => Task.FromResult(true);
        public Task<List<AgentInfo>> GetAccessibleSharedAgents() => Task.FromResult(new List<AgentInfo>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static AgentHealthStatus Healthy(string handle)
            => new() { Handle = handle, State = HealthState.Healthy, Timestamp = DateTime.UtcNow, IsConfigured = true };
    }
}
