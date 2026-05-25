using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Brain;
using FabrCore.Surface.Builders;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Rendering;
using FabrCore.Surface.Services;
using FabrCore.Surface.Templating;
using FabrCore.Surface.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FabrCore.Surface.Tests;

public sealed class SurfaceContractTests
{
    [Fact]
    public void AdaptiveCardSurfaceEnvelopeRoundTrips()
    {
        var envelope = new AdaptiveCardSurfaceEnvelope
        {
            Id = "card-1",
            Card = Json("""
                {
                  "type": "AdaptiveCard",
                  "version": "1.6",
                  "body": [
                    { "type": "TextBlock", "text": "Hello ${name}" }
                  ]
                }
                """),
            Data = Json("""{ "name": "Surface" }""")
        };

        var json = JsonSerializer.Serialize(envelope, SurfaceJson.Options);
        var roundTrip = JsonSerializer.Deserialize<AdaptiveCardSurfaceEnvelope>(json, SurfaceJson.Options);

        Assert.NotNull(roundTrip);
        Assert.Equal("2.0", roundTrip!.Version);
        Assert.Equal("card-1", roundTrip.Id);
        Assert.Equal("AdaptiveCard", roundTrip.Card.GetProperty("type").GetString());
        Assert.Equal("Surface", roundTrip.Data!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void TemplateExpanderMergesCardTemplateAndData()
    {
        var expanded = AdaptiveCardTemplateExpander.Expand(
            Json("""
                {
                  "type": "AdaptiveCard",
                  "version": "1.6",
                  "body": [
                    { "type": "TextBlock", "text": "Invoice ${invoice.number}" },
                    { "type": "TextBlock", "text": "${invoice.total}" }
                  ]
                }
                """),
            Json("""{ "invoice": { "number": "INV-1001", "total": 125.75 } }"""));

        var body = expanded.GetProperty("body");
        Assert.Equal("Invoice INV-1001", body[0].GetProperty("text").GetString());
        Assert.Equal(125.75, body[1].GetProperty("text").GetDouble());
    }

    [Fact]
    public void ValidatorAcceptsValidAdaptiveCardEnvelope()
    {
        var validator = new AdaptiveCardSurfaceValidator(Options.Create(new SurfaceOptions()));
        var envelope = ValidEnvelope();

        var result = validator.Validate(envelope);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ValidatorRejectsUnsafeOpenUrl()
    {
        var validator = new AdaptiveCardSurfaceValidator(Options.Create(new SurfaceOptions()));
        var envelope = ValidEnvelope("""
            {
              "type": "AdaptiveCard",
              "version": "1.6",
              "body": [],
              "actions": [
                { "type": "Action.OpenUrl", "title": "Open", "url": "javascript:alert(1)" }
              ]
            }
            """);

        var result = validator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not an allowed URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorRejectsUnknownActionType()
    {
        var options = new SurfaceOptions();
        options.AllowedActionTypes.Remove(AdaptiveCardActionTypes.OpenUrl);
        var validator = new AdaptiveCardSurfaceValidator(Options.Create(options));
        var envelope = ValidEnvelope("""
            {
              "type": "AdaptiveCard",
              "version": "1.6",
              "body": [],
              "actions": [
                { "type": "Action.OpenUrl", "title": "Open", "url": "https://example.com" }
              ]
            }
            """);

        var result = validator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Action.OpenUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorRejectsExcessivePayloadSize()
    {
        var options = new SurfaceOptions { MaxPayloadBytes = 20 };
        var validator = new AdaptiveCardSurfaceValidator(Options.Create(options));

        var result = validator.Validate(ValidEnvelope());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("maximum size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActionDispatcherRunsExecuteActionInApp()
    {
        var registry = new EchoActionRegistry();
        var dispatcher = new SurfaceActionDispatcher(registry, Options.Create(new SurfaceOptions()));
        var context = CreateRenderContext();

        await dispatcher.DispatchAsync(
            context,
            new AdaptiveCardSurfaceAction
            {
                Type = AdaptiveCardActionTypes.Execute,
                Verb = "approve",
                Data = Json("""{ "id": "INV-1001" }""")
            });

        Assert.Equal(1, registry.CallCount);
        Assert.Empty(((FakeSurfaceClientContext)context.ClientContext).SentMessages);
    }

    [Fact]
    public async Task ActionDispatcherRoutesSubmitActionToAppAndAgent()
    {
        var registry = new EchoActionRegistry();
        var dispatcher = new SurfaceActionDispatcher(registry, Options.Create(new SurfaceOptions()));
        var context = CreateRenderContext();

        await dispatcher.DispatchAsync(
            context,
            new AdaptiveCardSurfaceAction
            {
                Type = AdaptiveCardActionTypes.Submit,
                Data = Json("""
                    {
                      "actionId": "invoice.view",
                      "id": "INV-1001",
                      "routeTo": "both",
                      "messageTemplate": "show invoice {id}"
                    }
                    """)
            });

        Assert.Equal(1, registry.CallCount);
        var sent = Assert.Single(((FakeSurfaceClientContext)context.ClientContext).SentMessages);
        Assert.Equal(SurfaceMessageTypes.UiAction, sent.MessageType);
        Assert.Equal(SurfaceMessageTypes.DataType, sent.DataType);
        Assert.Equal("user1:assistant", sent.ToHandle);
        Assert.Equal("show invoice INV-1001", sent.Message);

        var actionEvent = JsonSerializer.Deserialize<AdaptiveCardActionEvent>(sent.Data!, SurfaceJson.Options);
        Assert.NotNull(actionEvent);
        Assert.Equal("invoice.view", actionEvent!.ActionId);
        Assert.Equal(SurfaceActionRoute.Both, actionEvent.RouteTo);
        Assert.Equal("INV-1001", actionEvent.Payload["id"]?.ToString());
    }

    [Fact]
    public async Task ActionDispatcherDoesNotDispatchClientOnlyActions()
    {
        var registry = new EchoActionRegistry();
        var dispatcher = new SurfaceActionDispatcher(registry, Options.Create(new SurfaceOptions()));
        var context = CreateRenderContext();

        await dispatcher.DispatchAsync(
            context,
            new AdaptiveCardSurfaceAction
            {
                Type = AdaptiveCardActionTypes.OpenUrl,
                Url = "https://example.com"
            });

        Assert.Equal(0, registry.CallCount);
        Assert.Empty(((FakeSurfaceClientContext)context.ClientContext).SentMessages);
    }

    [Fact]
    public void SurfaceEnvelopeExtractsFencedAdaptiveCardEnvelope()
    {
        var text = """
            Ready.

            ```fabrcore-adaptive-card-surface
            {
              "version": "2.0",
              "id": "approval",
              "card": {
                "type": "AdaptiveCard",
                "version": "1.6",
                "body": [
                  { "type": "TextBlock", "text": "Approve?" }
                ],
                "actions": [
                  { "type": "Action.Execute", "title": "Approve", "verb": "approve" }
                ]
              }
            }
            ```
            """;

        var envelope = SurfaceEnvelope.TryExtractEnvelope(text);

        Assert.NotNull(envelope);
        Assert.Equal("approval", envelope!.Id);
        Assert.Equal("AdaptiveCard", envelope.Card.GetProperty("type").GetString());
    }

    [Fact]
    public void SurfaceMessageFactoryCreatesAdaptiveCardRenderReply()
    {
        var source = new AgentMessage
        {
            FromHandle = "user1",
            ToHandle = "user1:agent",
            TraceId = "trace"
        };

        var message = SurfaceMessageFactory.CreateRenderMessage(ValidEnvelope(), source);

        Assert.Equal(MessageKind.OneWay, message.Kind);
        Assert.Equal("user1", message.ToHandle);
        Assert.Equal("user1:agent", message.FromHandle);
        Assert.Equal(SurfaceMessageTypes.UiRender, message.MessageType);
        Assert.Equal(SurfaceMessageTypes.DataType, message.DataType);
        Assert.NotNull(message.Data);
    }

    [Fact]
    public async Task FileSurfaceDefinitionProviderLoadsAdaptiveCardShape()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fabrcore-surface-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """
            {
              "surfaces": [
                {
                  "name": "accounting",
                  "planningModelName": "planner",
                  "maxAdaptiveCardVersion": "1.5",
                  "allowedActionTypes": [ "Action.Execute" ],
                  "allowedActionVerbs": [ "invoice.view" ],
                  "allowAnyActionVerb": false
                }
              ]
            }
            """);

        try
        {
            var provider = new FileSurfaceDefinitionProvider(
                new SurfaceAiOptions { DefinitionFilePath = file },
                NullLogger<FileSurfaceDefinitionProvider>.Instance);

            var definition = await provider.GetByNameAsync("accounting");

            Assert.NotNull(definition);
            Assert.Equal("planner", definition!.PlanningModelName);
            Assert.Equal("1.5", definition.MaxAdaptiveCardVersion);
            Assert.Contains("Action.Execute", definition.AllowedActionTypes);
            Assert.Contains("invoice.view", definition.AllowedActionVerbs);
            Assert.False(definition.AllowAnyActionVerb);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SurfaceHostServicesRegisterProducerSideWithoutClientRuntime()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddFabrCoreSurfaceServices(options =>
        {
            options.DefaultSurfaceDefinitionName = "accounting";
            options.DefaultPlanningModelName = "planner";
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ISurfaceProvider>());
        Assert.NotNull(provider.GetRequiredService<ISurfaceDefinitionProvider>());
        Assert.NotNull(provider.GetRequiredService<IOptions<SurfaceOptions>>());
        Assert.Equal("accounting", provider.GetRequiredService<SurfaceAiOptions>().DefaultSurfaceDefinitionName);
        Assert.Null(provider.GetService<ISurfaceClientContextFactory>());
    }

    [Fact]
    public async Task AddFabrCoreSurfaceFromConfigMapsProducerAndConsumerPolicy()
    {
        var file = WriteSurfaceConfig("""
            {
              "surfaces": [
                {
                  "name": "crm-demo",
                  "planningModelName": "default",
                  "maxAdaptiveCardVersion": "1.5",
                  "allowHttpUrls": false,
                  "allowAnyActionVerb": false,
                  "allowedActionTypes": [ "Action.Execute" ],
                  "allowedActionVerbs": [ "crm.customer.view" ],
                  "allowedTargetAgents": [ "crm-agent" ],
                  "enableDiagnostics": true
                }
              ]
            }
            """);

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.AddFabrCoreSurfaceFromConfig(file, "crm-demo");

            await using var provider = builder.Services.BuildServiceProvider();
            var surfaceOptions = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;
            var aiOptions = provider.GetRequiredService<SurfaceAiOptions>();

            Assert.Equal(file, aiOptions.DefinitionFilePath);
            Assert.Equal("crm-demo", aiOptions.DefaultSurfaceDefinitionName);
            Assert.Equal("default", aiOptions.DefaultPlanningModelName);
            Assert.Equal("1.5", surfaceOptions.MaxAdaptiveCardVersion);
            Assert.False(surfaceOptions.AllowAnyActionVerb);
            Assert.Contains("Action.Execute", surfaceOptions.AllowedActionTypes);
            Assert.Contains("crm.customer.view", surfaceOptions.AllowedActionVerbs);
            Assert.Contains("crm-agent", surfaceOptions.AllowedTargetAgents);
            Assert.True(surfaceOptions.EnableDiagnostics);
            Assert.NotNull(provider.GetRequiredService<ISurfaceProvider>());
            Assert.NotNull(provider.GetRequiredService<ISurfaceClientContextFactory>());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddFabrCoreSurfaceInlineConfigLoadsDefinitionPolicy()
    {
        var file = WriteSurfaceConfig("""
            {
              "surfaces": [
                {
                  "name": "crm-demo",
                  "planningModelName": "planner",
                  "allowedActionTypes": [ "Action.Execute" ],
                  "allowedActionVerbs": [ "crm.customer.view" ],
                  "allowAnyActionVerb": false
                }
              ]
            }
            """);

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.AddFabrCoreSurface(options =>
            {
                options.DefinitionFilePath = file;
                options.DefaultSurfaceDefinitionName = "crm-demo";
            });

            await using var provider = builder.Services.BuildServiceProvider();
            var surfaceOptions = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;
            var aiOptions = provider.GetRequiredService<SurfaceAiOptions>();

            Assert.Equal(file, aiOptions.DefinitionFilePath);
            Assert.Equal("crm-demo", aiOptions.DefaultSurfaceDefinitionName);
            Assert.Equal("planner", aiOptions.DefaultPlanningModelName);
            Assert.False(surfaceOptions.AllowAnyActionVerb);
            Assert.Contains("crm.customer.view", surfaceOptions.AllowedActionVerbs);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SurfaceActionsCreateCanonicalRoutedActionData()
    {
        var action = JsonSerializer.SerializeToElement(
            SurfaceActions.ToAgent(
                title: "View",
                verb: "crm.customer.view",
                targetAgent: "crm-agent",
                payload: new { customerId = "CUS-1001" },
                messageTemplate: "show me the customer view for customer {customerId}"),
            SurfaceJson.Options);

        Assert.Equal(AdaptiveCardActionTypes.Execute, action.GetProperty("type").GetString());
        Assert.Equal("View", action.GetProperty("title").GetString());
        Assert.Equal("crm.customer.view", action.GetProperty("verb").GetString());

        var data = action.GetProperty("data");
        Assert.Equal("crm.customer.view", data.GetProperty("actionId").GetString());
        Assert.Equal(SurfaceActionRoute.Agent, data.GetProperty("routeTo").GetString());
        Assert.Equal("crm-agent", data.GetProperty("targetAgent").GetString());
        Assert.Equal("CUS-1001", data.GetProperty("customerId").GetString());
        Assert.Equal("show me the customer view for customer {customerId}", data.GetProperty("messageTemplate").GetString());
    }

    [Fact]
    public async Task RenderAsyncHonorsTargetHandleMessageArgAndAddsDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFabrCoreSurfaceServices();
        services.Configure<SurfaceOptions>(options => options.EnableDiagnostics = true);
        using var provider = services.BuildServiceProvider();

        var host = new FakeAgentHost("user1:surface");
        var surfaceProvider = provider.GetRequiredService<ISurfaceProvider>();
        var service = await surfaceProvider.GetSurfaceServiceAsync(host, host.GetHandle(), "default");
        var source = new AgentMessage
        {
            FromHandle = "user1:crm-agent",
            ToHandle = "user1:surface",
            Args = new Dictionary<string, string>
            {
                [SurfaceMessageArgs.TargetHandle] = "demo-user"
            }
        };

        var message = await service.RenderAsync(ValidEnvelope(), source);

        Assert.Equal("demo-user", message.ToHandle);
        var sent = Assert.Single(host.SentMessages);
        Assert.Equal("demo-user", sent.ToHandle);
        Assert.Equal("demo-user", sent.Args![SurfaceDiagnosticArgs.TargetHandle]);
        Assert.Equal("1", sent.Args[SurfaceDiagnosticArgs.PlannedActionCount]);
        Assert.Equal("1", sent.Args[SurfaceDiagnosticArgs.ValidatedActionCount]);
        Assert.Equal("0", sent.Args[SurfaceDiagnosticArgs.RejectedActionCount]);
    }

    [Fact]
    public async Task FileSurfaceDefinitionProviderLoadsRequiredActions()
    {
        var file = WriteSurfaceConfig("""
            {
              "surfaces": [
                {
                  "name": "crm-demo",
                  "requiredActions": [
                    {
                      "appliesTo": "customer",
                      "title": "View",
                      "verb": "crm.customer.view",
                      "routeTo": "agent",
                      "targetAgent": "crm-agent",
                      "idField": "customerId",
                      "messageTemplate": "show customer {customerId}"
                    }
                  ]
                }
              ]
            }
            """);

        try
        {
            var provider = new FileSurfaceDefinitionProvider(
                new SurfaceAiOptions { DefinitionFilePath = file },
                NullLogger<FileSurfaceDefinitionProvider>.Instance);

            var definition = await provider.GetByNameAsync("crm-demo");

            var required = Assert.Single(definition!.RequiredActions);
            Assert.Equal("customer", required.AppliesTo);
            Assert.Equal("crm.customer.view", required.Verb);
            Assert.Equal("crm-agent", required.TargetAgent);

            var options = SurfaceDefinitionPolicyMapper.ToOptions(definition);
            Assert.Contains("crm.customer.view", options.AllowedActionVerbs);
            Assert.Contains("crm-agent", options.AllowedTargetAgents);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static AdaptiveCardSurfaceEnvelope ValidEnvelope(string? cardJson = null)
        => new()
        {
            Id = "valid",
            Card = Json(cardJson ?? """
                {
                  "type": "AdaptiveCard",
                  "version": "1.6",
                  "body": [
                    { "type": "TextBlock", "text": "Hello ${name}" }
                  ],
                  "actions": [
                    { "type": "Action.Execute", "title": "OK", "verb": "ok" }
                  ]
                }
                """),
            Data = Json("""{ "name": "Surface" }""")
        };

    private static SurfaceRenderContext CreateRenderContext()
        => new()
        {
            Envelope = ValidEnvelope(),
            SourceMessage = new AgentMessage
            {
                FromHandle = "user1:assistant",
                ToHandle = "user1",
                TraceId = "trace"
            },
            ClientContext = new FakeSurfaceClientContext("user1")
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static string WriteSurfaceConfig(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"fabrcore-surface-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private sealed class EchoActionRegistry : ISurfaceActionRegistry
    {
        public int CallCount { get; private set; }

        public Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SurfaceActionResult
            {
                Success = true,
                Data = new Dictionary<string, object?> { ["actionId"] = request.ActionId }
            });
        }
    }

    private sealed class FakeSurfaceClientContext : ISurfaceClientContext
    {
        public FakeSurfaceClientContext(string handle)
        {
            Handle = handle;
        }

        public string Handle { get; }

        public bool IsDisposed { get; private set; }

        public List<AgentMessage> SentMessages { get; } = [];

        public event EventHandler<AgentMessage>? AgentMessageReceived;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
            => Task.FromResult(request.Response());

        public Task SendMessage(AgentMessage request)
        {
            SentMessages.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEvent(EventMessage request, string? streamName = null)
            => Task.CompletedTask;

        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
            => Task.FromResult(NewHealth());

        public Task<AgentHealthStatus> ResetAgent(string handle)
            => Task.FromResult(NewHealth());

        public Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
            => Task.FromResult(NewHealth());

        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
            => Task.FromResult(new List<TrackedAgentInfo>());

        public Task<bool> IsAgentTracked(string handle)
            => Task.FromResult(false);

        public Task<List<AgentInfo>> GetAccessibleSharedAgents()
            => Task.FromResult(new List<AgentInfo>());

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Raise(AgentMessage message)
            => AgentMessageReceived?.Invoke(this, message);

        private AgentHealthStatus NewHealth()
            => new()
            {
                Handle = Handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true
            };
    }

    private sealed class FakeAgentHost : IFabrCoreAgentHost
    {
        public FakeAgentHost(string handle)
        {
            Handle = handle;
        }

        public string Handle { get; }

        public List<AgentMessage> SentMessages { get; } = [];

        public string GetHandle() => Handle;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
            => Task.FromResult(request.Response());

        public Task SendMessage(AgentMessage request)
        {
            SentMessages.Add(request);
            return Task.CompletedTask;
        }

        public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
            => Task.FromResult(new AgentHealthStatus
            {
                Handle = handle ?? Handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true
            });

        public Task SendEvent(EventMessage request, string? streamName = null)
            => Task.CompletedTask;

        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
        }

        public void UnregisterTimer(string timerName)
        {
        }

        public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
            => Task.CompletedTask;

        public Task UnregisterReminder(string reminderName)
            => Task.CompletedTask;

        public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId)
            => throw new NotSupportedException();

        public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
        {
        }

        public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
            => Task.FromResult(new List<StoredChatMessage>());

        public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task ClearThreadAsync(string threadId)
            => Task.CompletedTask;

        public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
            => Task.FromResult(new Dictionary<string, JsonElement>());

        public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
            => Task.CompletedTask;

        public void SetStatusMessage(string? message)
        {
        }
    }
}
