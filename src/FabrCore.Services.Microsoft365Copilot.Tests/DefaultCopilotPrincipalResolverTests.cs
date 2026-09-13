using System.Security.Claims;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class DefaultCopilotPrincipalResolverTests
{
    [TestMethod]
    public async Task CanonicalIdentity_IsIdenticalAcrossTeamsAndA2A_AndSeparatesApplications()
    {
        const string tenant = "11111111-2222-3333-4444-555555555555";
        const string oid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var teams = CreateResolver(o => o.Principal.Strategy = CopilotPrincipalStrategy.CanonicalEntra);
        var teamsHandle = await teams.ResolvePrincipalHandleAsync(TeamsContext(oid.ToUpperInvariant(), tenant), null, default);

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        FabrCore.Host.A2A.A2AExtensions.AddA2A(builder, o =>
        {
            o.Enabled = true;
            o.Authentication.Mode = FabrCore.Host.Configuration.A2AAuthenticationMode.JwtBearer;
            o.Authentication.JwtBearer.Authority = "https://login.microsoftonline.com/" + tenant + "/v2.0";
            o.Authentication.JwtBearer.Audience = "test-audience";
            o.Principal.Strategy = FabrCore.Host.Configuration.A2APrincipalStrategy.CanonicalEntra;
        });
        using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(builder.Services);
        var a2a = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<FabrCore.Host.A2A.IA2APrincipalResolver>(services);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", tenant), new Claim("oid", oid), new Claim("scp", "agent.invoke") }, "validated-token")),
        };
        var agent = new FabrCore.Host.A2A.A2AExposedAgent
        {
            Name = "assistant", BasePath = "/a2a/assistant", DisplayName = "Assistant", Description = "Assistant",
            Source = FabrCore.Host.A2A.A2AExposureSource.Configured, Models = "default", Plugins = [], Tools = [],
            Args = new Dictionary<string, string>(), AgentPerContext = false, InputModes = ["text/plain"], OutputModes = ["text/plain"],
            Streaming = true, Version = "1.0", Skills = [], Notes = [], HarnessSkills = [],
        };
        Assert.AreEqual(teamsHandle, await a2a.ResolvePrincipalHandleAsync(context, agent, "different-conversation"));
        Assert.AreEqual($"entra-{tenant}-{oid}", teamsHandle);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", tenant), new Claim("oid", oid), new Claim("idtyp", "app") }, "validated-token"));
        Assert.AreEqual($"app-{tenant}-{oid}", await a2a.ResolvePrincipalHandleAsync(context, agent, "same-conversation"));
        Assert.IsNull(await teams.ResolvePrincipalHandleAsync(TeamsContext(oid), null, default));
    }

    private static DefaultCopilotPrincipalResolver CreateResolver(Action<Microsoft365CopilotOptions>? configure = null)
    {
        var options = new Microsoft365CopilotOptions();
        configure?.Invoke(options);
        return new DefaultCopilotPrincipalResolver(
            Options.Create(options), NullLogger<DefaultCopilotPrincipalResolver>.Instance);
    }

    private static FakeTurnContext TeamsContext(string? aadObjectId, string? tenantId = null) => new(new Activity
    {
        Type = ActivityTypes.Message,
        ChannelId = Channels.Msteams,
        From = new ChannelAccount { Id = "29:user-channel-id", Name = "Eric", AadObjectId = aadObjectId, TenantId = tenantId },
        Conversation = new ConversationAccount { Id = "a:1conversation" },
    });

    [TestMethod]
    public async Task MapsEntraObjectId_ByDefault()
    {
        var handle = await CreateResolver().ResolvePrincipalHandleAsync(
            TeamsContext("AAAA1111-0000-0000-0000-000000000000"), null, CancellationToken.None);

        Assert.AreEqual("aaaa1111-0000-0000-0000-000000000000", handle);
    }

    [TestMethod]
    public async Task RejectsUser_WithoutEntraIdentity_WhenValidationEnabled()
    {
        var handle = await CreateResolver().ResolvePrincipalHandleAsync(
            TeamsContext(aadObjectId: null), null, CancellationToken.None);

        Assert.IsNull(handle);
    }

    [TestMethod]
    public async Task FallsBackToChannelUser_WhenValidationDisabled()
    {
        var resolver = CreateResolver(o => o.TokenValidation.Enabled = false);

        var handle = await resolver.ResolvePrincipalHandleAsync(
            TeamsContext(aadObjectId: null), null, CancellationToken.None);

        Assert.AreEqual("msteams-29-user-channel-id", handle);
    }

    [TestMethod]
    public async Task AppliesPrefix()
    {
        var resolver = CreateResolver(o => o.Principal.Prefix = "m365-");

        var handle = await resolver.ResolvePrincipalHandleAsync(
            TeamsContext("BBBB2222-0000-0000-0000-000000000000"), null, CancellationToken.None);

        Assert.AreEqual("m365-bbbb2222-0000-0000-0000-000000000000", handle);
    }

    [TestMethod]
    public async Task ComposesTenantAndObjectId()
    {
        var resolver = CreateResolver(o => o.Principal.Strategy = CopilotPrincipalStrategy.TenantAndObjectId);

        var handle = await resolver.ResolvePrincipalHandleAsync(
            TeamsContext("cccc3333-0000-0000-0000-000000000000", tenantId: "tttt0000-0000-0000-0000-000000000000"),
            null, CancellationToken.None);

        Assert.AreEqual("tttt0000-0000-0000-0000-000000000000-cccc3333-0000-0000-0000-000000000000", handle);
    }

    /// <summary>Minimal ITurnContext exposing only what the resolver reads.</summary>
    private sealed class FakeTurnContext : ITurnContext
    {
        public FakeTurnContext(IActivity activity) => Activity = activity;

        public IActivity Activity { get; }
        public ClaimsIdentity Identity => new();

        public IChannelAdapter Adapter => throw new NotSupportedException();
        public TurnContextStateCollection Services => throw new NotSupportedException();
        public TurnContextStateCollection StackState => throw new NotSupportedException();
        public IStreamingResponse StreamingResponse => throw new NotSupportedException();
        public bool Responded => false;

        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => throw new NotSupportedException();
        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => throw new NotSupportedException();
        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => throw new NotSupportedException();
        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> TraceActivityAsync(string name, object? value = null, string? valueType = null, string? label = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
