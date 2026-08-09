#pragma warning disable MAAI001 // Harness providers are for evaluation purposes only and may change.
using System.Text.Json;
using System.Text;
using System.Security.Claims;
using System.Net;
using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.Monitoring;
using FabrCore.Sdk;
using FabrCore.Surface;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Actions;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Squads;
using FabrCore.Surface.Ai.Tasks;
using FabrCore.Surface.Brain;
using FabrCore.Surface.Builders;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Components;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Identity;
using FabrCore.Surface.Services;
using FabrCore.Surface.Templating;
using FabrCore.Surface.Validation;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FabrCore.Surface.Tests;

// Declared inside the namespace so the alias beats the Orleans-backed
// FabrCore.Surface.SurfacePrincipalContext during enclosing-namespace lookup.
using SurfacePrincipalContext = FabrCore.Surface.Identity.SurfacePrincipalContext;

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
    public void ValidatorDoesNotBlockUnknownBusinessActionVerbs()
    {
        var validator = new AdaptiveCardSurfaceValidator(Options.Create(new SurfaceOptions()));
        var envelope = ValidEnvelope("""
            {
              "type": "AdaptiveCard",
              "version": "1.6",
              "body": [],
              "actions": [
                { "type": "Action.Execute", "title": "Do It", "verb": "unknown.business.verb" }
              ]
            }
            """);

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
        var context = CreateActionContext();

        await dispatcher.DispatchAsync(
            context,
            new AdaptiveCardSurfaceAction
            {
                Type = AdaptiveCardActionTypes.Execute,
                Verb = "approve",
                Data = Json("""{ "id": "INV-1001" }""")
            });

        Assert.Equal(1, registry.CallCount);
        Assert.Empty(((FakeSurfacePrincipalContext)context.PrincipalContext).SentMessages);
    }

    [Fact]
    public async Task ActionDispatcherRoutesSubmitActionToAppAndAgent()
    {
        var registry = new EchoActionRegistry();
        var dispatcher = new SurfaceActionDispatcher(registry, Options.Create(new SurfaceOptions()));
        var context = CreateActionContext();

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
        var sent = Assert.Single(((FakeSurfacePrincipalContext)context.PrincipalContext).SentMessages);
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
        var context = CreateActionContext();

        await dispatcher.DispatchAsync(
            context,
            new AdaptiveCardSurfaceAction
            {
                Type = AdaptiveCardActionTypes.OpenUrl,
                Url = "https://example.com"
            });

        Assert.Equal(0, registry.CallCount);
        Assert.Empty(((FakeSurfacePrincipalContext)context.PrincipalContext).SentMessages);
    }

    [Fact]
    public async Task SurfaceMonitorClientReadsStringEnumValues()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal("https://fabrcore.example/fabrcoreapi/Monitor/messages?limit=25", request.RequestUri?.ToString());
            Assert.True(request.Headers.TryGetValues("x-user", out var values));
            Assert.Equal("user1", Assert.Single(values));
            Assert.True(request.Headers.TryGetValues("x-user-handle", out var principalHandleValues));
            Assert.Equal("user1", Assert.Single(principalHandleValues));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "count": 1,
                      "limit": 25,
                      "messages": [
                        {
                          "id": "msg-1",
                          "agentHandle": "user1:assistant",
                          "fromHandle": "user1",
                          "toHandle": "user1:assistant",
                          "message": "hello",
                          "messageType": "chat",
                          "kind": "request",
                          "direction": "inbound",
                          "timestamp": "2026-05-30T12:00:00Z",
                          "busyRouted": false
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var client = new SurfaceMonitorClient(
            httpClient,
            Options.Create(new SurfaceOptions { FabrCoreHostUrl = "https://fabrcore.example" }),
            NullLogger<SurfaceMonitorClient>.Instance);

        var response = await client.GetMessagesAsync("user1");

        var message = Assert.Single(response.Messages);
        Assert.Equal(MessageKind.Request, message.Kind);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
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
        Assert.Equal("user1", message.Args![SurfaceMessageArgs.SurfaceSourceHandle]);
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
                  "allowedTargetAgents": [ "accounting-agent" ]
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
            Assert.Contains("accounting-agent", definition.AllowedTargetAgents);
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
        Assert.Null(provider.GetService<ISurfacePrincipalContextFactory>());
    }

    [Fact]
    public void SurfaceComponentsEnableOnlySurfaceNavigation()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreSurfaceComponents();

        using var provider = services.BuildServiceProvider();
        var navigation = provider.GetRequiredService<IOptions<SurfaceNavigationOptions>>().Value;

        Assert.True(navigation.SurfaceLoaded);
        Assert.False(navigation.AdminLoaded);
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
                  "allowedActionTypes": [ "Action.Execute" ],
                  "allowedTargetAgents": [ "crm-agent" ],
                  "enableDiagnostics": true
                }
              ]
            }
            """);

        try
        {
            var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

            builder.AddFabrCoreSurfaceFromConfig(file, "crm-demo");

            await using var provider = builder.Services.BuildServiceProvider();
            var surfaceOptions = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;
            var aiOptions = provider.GetRequiredService<SurfaceAiOptions>();

            Assert.Equal(file, aiOptions.DefinitionFilePath);
            Assert.Equal("crm-demo", aiOptions.DefaultSurfaceDefinitionName);
            Assert.Equal("default", aiOptions.DefaultPlanningModelName);
            Assert.Equal("1.5", surfaceOptions.MaxAdaptiveCardVersion);
            Assert.Contains("Action.Execute", surfaceOptions.AllowedActionTypes);
            Assert.Contains("crm-agent", surfaceOptions.AllowedTargetAgents);
            Assert.True(surfaceOptions.EnableDiagnostics);
            Assert.NotNull(provider.GetRequiredService<ISurfaceProvider>());
            Assert.NotNull(provider.GetRequiredService<ISurfacePrincipalContextFactory>());
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
                  "allowedTargetAgents": [ "crm-agent" ]
                }
              ]
            }
            """);

        try
        {
            var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

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
            Assert.Contains("crm-agent", surfaceOptions.AllowedTargetAgents);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddFabrCoreSurfaceCopiesDefaultSurfaceAgentHandles()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.AddFabrCoreSurface(options =>
        {
            options.DefaultSurfaceAgentHandles.Add("crm-agent");
            options.DefaultSurfaceAgentHandles.Add("owner1:analyst");
        });

        await using var provider = builder.Services.BuildServiceProvider();
        var surfaceOptions = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;

        Assert.Contains("crm-agent", surfaceOptions.DefaultSurfaceAgentHandles);
        Assert.Contains("owner1:analyst", surfaceOptions.DefaultSurfaceAgentHandles);
    }

    [Fact]
    public void SurfacePreferencesDefaultsSeedSurfaceAgentHandles()
    {
        var options = new SurfaceOptions
        {
            ShowHiddenAgentsByDefault = true,
            ShowRunningAgentsByDefault = true
        };
        options.DefaultSurfaceAgentHandles.Add("crm-agent");
        options.DefaultSurfaceAgentHandles.Add(" ");

        var preferences = SurfacePreferences.FromDefaults(options);

        Assert.True(preferences.ShowHiddenAgents);
        Assert.True(preferences.ShowRunningAgents);
        Assert.Contains("crm-agent", preferences.SurfaceAgentHandles);
        Assert.DoesNotContain(" ", preferences.SurfaceAgentHandles);
    }

    [Fact]
    public async Task AddFabrCoreSurfaceFromMinimalConfigUsesAdaptiveCardDefaults()
    {
        var file = WriteSurfaceConfig("""
            {
              "surfaces": [
                {
                  "name": "minimal",
                  "planningModelName": "planner"
                }
              ]
            }
            """);

        try
        {
            var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

            builder.AddFabrCoreSurfaceFromConfig(file, "minimal");

            await using var provider = builder.Services.BuildServiceProvider();
            var surfaceOptions = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;
            var aiOptions = provider.GetRequiredService<SurfaceAiOptions>();

            Assert.Equal("planner", aiOptions.DefaultPlanningModelName);
            Assert.Equal("1.6", surfaceOptions.MaxAdaptiveCardVersion);
            Assert.False(surfaceOptions.AllowHttpUrls);
            Assert.Equal(
                AdaptiveCardActionTypes.Defaults.Order(StringComparer.OrdinalIgnoreCase),
                surfaceOptions.AllowedActionTypes.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(surfaceOptions.AllowedTargetAgents);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task DefaultPrincipalProviderUsesConfiguredClaimPrecedence()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "subject-principal"),
                    new Claim("oid", "object-principal"),
                    new Claim(ClaimTypes.Name, "Surface Principal")
                ],
                "test"))
        };
        var provider = CreatePrincipalProvider(httpContext, new SurfaceOptions
        {
            PrincipalClaimTypes = ["oid", "sub"]
        });

        var principal = await provider.GetCurrentAsync();

        Assert.True(principal.IsResolved);
        Assert.Equal("object-principal", principal.PrincipalId);
        Assert.Equal("Surface Principal", principal.DisplayName);
        Assert.Equal("oid", principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderFailsClosedWithoutAnySource()
    {
        var provider = CreatePrincipalProvider(new DefaultHttpContext());

        var principal = await provider.GetCurrentAsync();

        Assert.False(principal.IsResolved);
        Assert.Null(principal.PrincipalId);
    }

    [Fact]
    public async Task DefaultPrincipalProviderUsesConfiguredDevelopmentFallback()
    {
        var provider = CreatePrincipalProvider(
            new DefaultHttpContext(),
            new SurfaceOptions { DevelopmentFallbackPrincipalId = "Dev-Principal" });

        var principal = await provider.GetCurrentAsync();

        Assert.True(principal.IsResolved);
        Assert.Equal("dev-principal", principal.PrincipalId);
        Assert.False(principal.IsAuthenticated);
        Assert.Equal(nameof(SurfaceOptions.DevelopmentFallbackPrincipalId), principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderResolvesConfiguredHeader()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "User.One@Contoso.COM";
        httpContext.Request.Headers["X-User-Name"] = "User One";
        var provider = CreatePrincipalProvider(httpContext, new SurfaceOptions
        {
            PrincipalHeaderNames = ["X-User-Id"]
        });

        var principal = await provider.GetCurrentAsync();

        Assert.True(principal.IsResolved);
        Assert.True(principal.IsAuthenticated);
        Assert.Equal("user.one-contoso.com", principal.PrincipalId);
        Assert.Equal("User One", principal.DisplayName);
        Assert.Equal("header:X-User-Id", principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderIgnoresHeadersWhenNotConfigured()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "spoofed-user";
        var provider = CreatePrincipalProvider(httpContext);

        var principal = await provider.GetCurrentAsync();

        Assert.False(principal.IsResolved);
    }

    [Fact]
    public async Task DefaultPrincipalProviderHeaderWinsOverClaims()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", "claims-principal")],
                "test"))
        };
        httpContext.Request.Headers["X-User-Id"] = "header-principal";
        var provider = CreatePrincipalProvider(httpContext, new SurfaceOptions
        {
            PrincipalHeaderNames = ["X-User-Id"]
        });

        var principal = await provider.GetCurrentAsync();

        Assert.Equal("header-principal", principal.PrincipalId);
        Assert.Equal("header:X-User-Id", principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderAmbientAccessorWinsOverHeaderAndClaims()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", "claims-principal")],
                "test"))
        };
        httpContext.Request.Headers["X-User-Id"] = "header-principal";
        var accessor = new SurfacePrincipalAccessor
        {
            Principal = new SurfacePrincipalContext("Ambient@Principal", "Ambient Principal", true, null)
        };
        var provider = CreatePrincipalProvider(
            httpContext,
            new SurfaceOptions { PrincipalHeaderNames = ["X-User-Id"] },
            accessor);

        var principal = await provider.GetCurrentAsync();

        Assert.Equal("ambient-principal", principal.PrincipalId);
        Assert.Equal("ambient", principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderResolverDelegateWins()
    {
        var provider = CreatePrincipalProvider(new DefaultHttpContext(), new SurfaceOptions
        {
            DevelopmentFallbackPrincipalId = "dev-principal",
            PrincipalResolver = (_, _) => Task.FromResult<SurfacePrincipalContext?>(
                new SurfacePrincipalContext("Resolver@Principal", "Resolver Principal", true, null))
        });

        var principal = await provider.GetCurrentAsync();

        Assert.Equal("resolver-principal", principal.PrincipalId);
        Assert.Equal("resolver", principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderResolverNullFallsThrough()
    {
        var provider = CreatePrincipalProvider(new DefaultHttpContext(), new SurfaceOptions
        {
            DevelopmentFallbackPrincipalId = "dev-principal",
            PrincipalResolver = (_, _) => Task.FromResult<SurfacePrincipalContext?>(null)
        });

        var principal = await provider.GetCurrentAsync();

        Assert.Equal("dev-principal", principal.PrincipalId);
        Assert.Equal(nameof(SurfaceOptions.DevelopmentFallbackPrincipalId), principal.Source);
    }

    [Fact]
    public async Task DefaultPrincipalProviderNormalizationCanBeDisabled()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "User.One@Contoso.COM";
        var provider = CreatePrincipalProvider(httpContext, new SurfaceOptions
        {
            PrincipalHeaderNames = ["X-User-Id"],
            NormalizePrincipalIds = false
        });

        var principal = await provider.GetCurrentAsync();

        Assert.Equal("User.One@Contoso.COM", principal.PrincipalId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" User.One@Contoso.COM ", "user.one-contoso.com")]
    [InlineData("ABC_def-123.", "abc_def-123.")]
    [InlineData("a b:c/d", "a-b-c-d")]
    public void SurfacePrincipalIdNormalizeRules(string? input, string? expected)
    {
        Assert.Equal(expected, SurfacePrincipalId.Normalize(input));
    }

    [Fact]
    public async Task DefaultPrincipalProviderMemoizesAuthenticatedResolutionPerScope()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "circuit-principal";
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var provider = new DefaultSurfacePrincipalContextProvider(
            httpContextAccessor,
            Options.Create(new SurfaceOptions { PrincipalHeaderNames = ["X-User-Id"] }),
            new SurfacePrincipalAccessor());

        var first = await provider.GetCurrentAsync();
        httpContextAccessor.HttpContext = null;
        var second = await provider.GetCurrentAsync();

        Assert.Equal("circuit-principal", first.PrincipalId);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DefaultPrincipalProviderDoesNotMemoizeFallbackOrUnresolved()
    {
        var accessor = new SurfacePrincipalAccessor();
        var provider = CreatePrincipalProvider(
            new DefaultHttpContext(),
            new SurfaceOptions { DevelopmentFallbackPrincipalId = "dev-principal" },
            accessor);

        var first = await provider.GetCurrentAsync();
        accessor.Principal = new SurfacePrincipalContext("ambient-principal", "Ambient", true, "circuit");
        var second = await provider.GetCurrentAsync();

        Assert.Equal("dev-principal", first.PrincipalId);
        Assert.Equal("ambient-principal", second.PrincipalId);
        Assert.Equal("circuit", second.Source);
    }

    [Fact]
    public void SurfacePrincipalContextRoundTripsThroughJson()
    {
        var principal = new SurfacePrincipalContext("user.one", "User One", true, "header:X-User-Id");

        var restored = JsonSerializer.Deserialize<SurfacePrincipalContext>(JsonSerializer.Serialize(principal));

        Assert.NotNull(restored);
        Assert.Equal(principal.PrincipalId, restored.PrincipalId);
        Assert.Equal(principal.DisplayName, restored.DisplayName);
        Assert.Equal(principal.IsAuthenticated, restored.IsAuthenticated);
        Assert.Equal(principal.Source, restored.Source);
        Assert.True(restored.IsResolved);
    }

    private static DefaultSurfacePrincipalContextProvider CreatePrincipalProvider(
        HttpContext? httpContext,
        SurfaceOptions? options = null,
        SurfacePrincipalAccessor? accessor = null,
        IServiceProvider? serviceProvider = null)
        => new(
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(options ?? new SurfaceOptions()),
            accessor ?? new SurfacePrincipalAccessor(),
            serviceProvider);

    [Fact]
    public async Task AddFabrCoreSurfaceCopiesCommandCenterOptions()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        Func<IServiceProvider, CancellationToken, Task<SurfacePrincipalContext?>> resolver =
            (_, _) => Task.FromResult<SurfacePrincipalContext?>(null);

        builder.AddFabrCoreSurface(options =>
        {
            options.PrincipalClaimTypes = ["custom-principal"];
            options.PrincipalHeaderNames = ["X-User-Id"];
            options.PrincipalDisplayNameHeaderNames = ["X-Display-Name"];
            options.NormalizePrincipalIds = false;
            options.PrincipalResolver = resolver;
            options.DevelopmentFallbackPrincipalId = "dev-principal";
            options.EnableAgentChat = false;
            options.CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.RequestResponse;
            options.CommandCenterChatMessageKind = SurfaceChatMessageKind.OneWay;
            options.EnableAgentCreate = true;
            options.FabrCoreHostUrl = "https://fabrcore.example";
            options.EnableDiagnosticsPanel = true;
        });

        await using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;

        Assert.Equal(["custom-principal"], options.PrincipalClaimTypes);
        Assert.Equal(["X-User-Id"], options.PrincipalHeaderNames);
        Assert.Equal(["X-Display-Name"], options.PrincipalDisplayNameHeaderNames);
        Assert.False(options.NormalizePrincipalIds);
        Assert.Same(resolver, options.PrincipalResolver);
        Assert.Equal("dev-principal", options.DevelopmentFallbackPrincipalId);
        Assert.False(options.EnableAgentChat);
        Assert.Equal(SurfaceChatDeliveryMode.RequestResponse, options.CommandCenterChatDeliveryMode);
        Assert.Equal(SurfaceChatMessageKind.OneWay, options.CommandCenterChatMessageKind);
        Assert.True(options.EnableAgentCreate);
        Assert.Equal("https://fabrcore.example", options.FabrCoreHostUrl);
        Assert.True(options.EnableDiagnosticsPanel);
        Assert.NotNull(provider.GetRequiredService<ISurfacePrincipalContextProvider>());
        Assert.NotNull(provider.GetRequiredService<SurfacePrincipalAccessor>());
        Assert.NotNull(provider.GetRequiredService<SurfaceWorkspaceService>());
        Assert.NotNull(provider.GetRequiredService<ISurfaceDiscoveryClient>());
        Assert.NotNull(provider.GetRequiredService<ISurfaceSquadService>());
        Assert.NotNull(provider.GetRequiredService<ISurfaceSquadConfigClient>());
    }

    [Fact]
    public async Task AddFabrCoreSurfaceUsesConfiguredHostUrlForDiscovery()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["FabrCore:HostUrl"] = "https://configured.example";

        builder.AddFabrCoreSurface();

        await using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SurfaceOptions>>().Value;

        Assert.Equal("https://configured.example", options.FabrCoreHostUrl);
    }

    [Fact]
    public async Task SurfaceDiscoveryClientDeserializesHostDiscoveryShape()
    {
        var httpClient = new HttpClient(new JsonResponseHandler("""
            {
              "agents": [
                {
                  "typeName": "Demo.AssistantAgent",
                  "aliases": [ "assistant" ],
                  "description": "General assistant",
                  "capabilities": "Chat",
                  "notes": [ "Use for demos" ]
                }
              ],
              "plugins": [],
              "tools": [
                {
                  "typeName": "Demo.Tools.Clock.GetTime",
                  "aliases": [ "GetTime" ],
                  "methods": [
                    { "name": "GetTime", "description": "Gets time" }
                  ]
                }
              ],
              "collisions": [
                { "alias": "assistant", "category": "agent", "types": [ "A", "B" ] }
              ]
            }
            """));
        var client = new SurfaceDiscoveryClient(
            httpClient,
            Options.Create(new SurfaceOptions { FabrCoreHostUrl = "https://fabrcore.example" }),
            NullLogger<SurfaceDiscoveryClient>.Instance);

        var discovery = await client.GetDiscoveryAsync();

        var agent = Assert.Single(discovery.Agents);
        Assert.Equal("assistant", Assert.Single(agent.Aliases));
        Assert.Equal("General assistant", agent.Description);
        var tool = Assert.Single(discovery.Tools);
        Assert.Equal("GetTime", Assert.Single(tool.Methods).Name);
        Assert.Equal("assistant", Assert.Single(discovery.Collisions!).Alias);
    }

    [Fact]
    public void SurfaceSquadAgentAliasesAreDiscoverableByFabrCoreRegistry()
    {
        var registry = new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance);
        var aliases = registry.GetAgentTypes().SelectMany(entry => entry.Aliases).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(SurfaceTaskAgentTypes.TaskRunner, aliases);
        Assert.Contains(SurfaceOrchestrationAgentTypes.SquadOrchestrator, aliases);
    }

    [Fact]
    public void SurfaceSquadCapabilityProjectionPrefersMemberDescription()
    {
        var registry = new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance);
        var registryEntry = registry.GetAgentTypes()
            .First(entry => entry.Aliases.Contains("surface-test-routing-agent", StringComparer.OrdinalIgnoreCase));
        var squadAgent = new SurfaceSquadAgent
        {
            Name = "routing-test",
            Handle = "owner1:routing-test",
            AgentType = "surface-test-routing-agent",
            Description = "Fallback description"
        };
        var health = new AgentHealthStatus
        {
            Handle = "owner1:routing-test",
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
            Configuration = new AgentConfiguration
            {
                Plugins = ["crm"],
                Tools = ["lookup"]
            }
        };

        var capability = SurfaceSquadAgentCapabilityProjection.Build(squadAgent, registryEntry, health);

        Assert.Equal("routing-test", capability.Name);
        Assert.True(capability.IsConfigured);
        Assert.StartsWith("Fallback description", capability.Description, StringComparison.Ordinal);
        Assert.Contains("Capabilities: Handles routing projection tests", capability.Description);
        Assert.DoesNotContain("Registry description", capability.Description);
        Assert.Contains("Prefer this test agent", capability.Notes);
        Assert.Equal(["crm"], capability.Plugins);
        Assert.Equal(["lookup"], capability.Tools);
    }

    [Fact]
    public void SurfaceSquadCapabilityProjectionUsesHealthDescriptionBeforeRegistry()
    {
        var registry = new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance);
        var registryEntry = registry.GetAgentTypes()
            .First(entry => entry.Aliases.Contains("surface-test-routing-agent", StringComparer.OrdinalIgnoreCase));
        var squadAgent = new SurfaceSquadAgent
        {
            Name = "routing-test",
            Handle = "owner1:routing-test",
            AgentType = "surface-test-routing-agent"
        };
        var health = new AgentHealthStatus
        {
            Handle = "owner1:routing-test",
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
            Configuration = new AgentConfiguration
            {
                Description = "Instance health description"
            }
        };

        var capability = SurfaceSquadAgentCapabilityProjection.Build(squadAgent, registryEntry, health);

        Assert.StartsWith("Instance health description", capability.Description, StringComparison.Ordinal);
        Assert.Contains("Capabilities: Handles routing projection tests", capability.Description);
        Assert.DoesNotContain("Registry description", capability.Description);
    }

    [Fact]
    public void SurfaceSquadCapabilityProjectionFallsBackToRegistryDescription()
    {
        var registry = new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance);
        var registryEntry = registry.GetAgentTypes()
            .First(entry => entry.Aliases.Contains("surface-test-routing-agent", StringComparer.OrdinalIgnoreCase));
        var squadAgent = new SurfaceSquadAgent
        {
            Name = "routing-test",
            Handle = "owner1:routing-test",
            AgentType = "surface-test-routing-agent"
        };

        var capability = SurfaceSquadAgentCapabilityProjection.Build(squadAgent, registryEntry, health: null);

        Assert.StartsWith("Registry description", capability.Description, StringComparison.Ordinal);
        Assert.Contains("Capabilities: Handles routing projection tests", capability.Description);
    }

    [Fact]
    public void SurfaceSquadServiceKeepsNestedSquadMemberDiscovery()
    {
        var definition = new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Operations Hub",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Handle = SurfaceSquadHandleBuilder.BuildOrchestratorAlias("Research Squad"),
                    Name = "Research Squad",
                    AgentType = SurfaceOrchestrationAgentTypes.SquadOrchestrator,
                    Models = "default",
                    Description = "Researches contracts, case notes, and source documents for this workflow.",
                    Role = SurfaceSquadMemberRole.Executor
                }
            ]
        };

        var squad = SurfaceSquadService.BuildSquad("owner1", definition);
        var member = Assert.Single(squad.Agents);

        Assert.Equal("owner1:squad-research-squad", member.Handle);
        Assert.Equal("Research Squad", member.Name);
        Assert.Equal(SurfaceOrchestrationAgentTypes.SquadOrchestrator, member.AgentType);
        Assert.Equal("Researches contracts, case notes, and source documents for this workflow.", member.Description);
    }

    [Fact]
    public async Task SurfaceWorkspaceChatUsesFireAndForgetRequestByDefault()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        var sent = Assert.Single(context.SentMessages);
        Assert.Equal(MessageKind.Request, sent.Kind);
        Assert.Equal("chat", sent.MessageType);
        Assert.Equal("owner1:assistant", sent.ToHandle);
        Assert.Empty(context.RequestMessages);
        Assert.Contains(context.GetAgentHealthCalls, call =>
            call.Handle == "owner1:assistant"
            && call.DetailLevel == HealthDetailLevel.Basic);
        Assert.Contains(workspace.Timeline, item => item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "hello");
    }

    [Fact]
    public async Task SurfaceWorkspaceChatShowsFriendlyMessageWhenAgentIsNotConfigured()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.AgentHealthStatuses["owner1:assistant"] = new AgentHealthStatus
        {
            Handle = "owner1:assistant",
            State = HealthState.NotConfigured,
            Timestamp = DateTime.UtcNow,
            IsConfigured = false,
            Message = "Agent not configured"
        };
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        Assert.Empty(context.SentMessages);
        Assert.Empty(context.RequestMessages);
        Assert.DoesNotContain(workspace.Timeline, item => item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "hello");
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.AgentHandle == "owner1:assistant"
            && item.Text!.Contains("has not been configured yet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SurfaceWorkspaceChatShowsFriendlyMessageWhenAgentHealthIsBad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.AgentHealthStatuses["owner1:assistant"] = new AgentHealthStatus
        {
            Handle = "owner1:assistant",
            State = HealthState.Unhealthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
            Message = "Model configuration missing"
        };
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        Assert.Empty(context.SentMessages);
        Assert.Empty(context.RequestMessages);
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.AgentHandle == "owner1:assistant"
            && item.Text!.Contains("unavailable right now", StringComparison.OrdinalIgnoreCase)
            && item.Text.Contains("Model configuration missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SurfaceWorkspaceChatReportsHealthCheckFailureBeforeSending()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.GetAgentHealthException = new TimeoutException("health timed out");
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        Assert.Empty(context.SentMessages);
        Assert.Empty(context.RequestMessages);
        Assert.Contains(context.GetAgentHealthCalls, call => call.Handle == "owner1:assistant");
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.AgentHandle == "owner1:assistant"
            && item.Text!.Contains("couldn't reach assistant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SurfaceWorkspaceChatCanSendToExplicitAgentWithoutChangingSelection()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");
        Assert.Equal("owner1:assistant", workspace.SelectedAgent?.Handle);

        await workspace.SendChatAsync("review this", "analyst");

        var sent = Assert.Single(context.SentMessages);
        Assert.Equal("owner1:analyst", sent.ToHandle);
        Assert.Equal("owner1:assistant", workspace.SelectedAgent?.Handle);
        Assert.Contains(workspace.GetTimelineForAgent("owner1:analyst"), item =>
            item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "review this");
    }

    [Fact]
    public async Task SurfaceWorkspaceFireAndForgetUsesPrincipalContextWhenDirectSenderIsAvailable()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var directSender = new FakeSurfaceDirectMessageSender();
        await using var provider = new ServiceCollection()
            .AddSingleton<ISurfaceDirectMessageSender>(directSender)
            .BuildServiceProvider();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            provider);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        Assert.Empty(directSender.SentMessages);
        var sent = Assert.Single(context.SentMessages);
        Assert.Equal("owner1", sent.FromHandle);
        Assert.Equal("owner1:assistant", sent.ToHandle);
        Assert.Equal(MessageKind.Request, sent.Kind);
        Assert.Equal("hello", sent.Message);
    }

    [Fact]
    public async Task SurfaceWorkspaceTranscriptSharesOwnerSendsAcrossScopes()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        await using var provider = CreateSurfaceServiceProvider(context, options =>
        {
            options.ShowRunningAgentsByDefault = true;
        });
        await using var commandScope = provider.CreateAsyncScope();
        await using var linkScope = provider.CreateAsyncScope();
        var commandWorkspace = commandScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var linkWorkspace = linkScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var principal = new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test");

        await commandWorkspace.InitializeAsync(principal);
        await linkWorkspace.InitializeAsync(principal);
        await linkWorkspace.SendChatAsync("Hello", "assistant");

        Assert.Contains(commandWorkspace.GetTimelineForAgent("owner1:assistant"), item =>
            item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "Hello");
        Assert.Contains(linkWorkspace.GetTimelineForAgent("owner1:assistant"), item =>
            item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "Hello");
    }

    [Fact]
    public async Task SurfaceWorkspaceTranscriptDeduplicatesFanOutResponsesAcrossScopes()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        await using var provider = CreateSurfaceServiceProvider(context, options =>
        {
            options.ShowRunningAgentsByDefault = true;
        });
        await using var commandScope = provider.CreateAsyncScope();
        await using var linkScope = provider.CreateAsyncScope();
        var commandWorkspace = commandScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var linkWorkspace = linkScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var principal = new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test");

        await commandWorkspace.InitializeAsync(principal);
        await linkWorkspace.InitializeAsync(principal);

        context.Raise(new AgentMessage
        {
            Id = "response-1",
            FromHandle = "owner1:assistant",
            ToHandle = "owner1",
            MessageType = "chat",
            Message = "Hello from the agent"
        });

        Assert.Single(commandWorkspace.GetTimelineForAgent("owner1:assistant"),
            item => item.Kind == SurfaceTimelineItemKind.Agent && item.Text == "Hello from the agent");
        Assert.Single(linkWorkspace.GetTimelineForAgent("owner1:assistant"),
            item => item.Kind == SurfaceTimelineItemKind.Agent && item.Text == "Hello from the agent");
    }

    [Fact]
    public async Task SurfaceWorkspaceUnreadSummariesIncludeAgentsAndSquads()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:ops-squad", SurfaceOrchestrationAgentTypes.SquadOrchestrator));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");

        RaiseChat(context, "owner1:analyst", "First analyst note", "analyst-1");
        RaiseChat(context, "owner1:analyst", "Second analyst note", "analyst-2");
        RaiseChat(context, "owner1:ops-squad", "Squad update", "squad-1");

        Assert.Equal(3, workspace.TotalUnreadCount);
        var summaries = workspace.GetUnreadSummaries();
        var analyst = Assert.Single(summaries, summary => summary.Handle == "owner1:analyst");
        Assert.Equal("analyst", analyst.DisplayName);
        Assert.Equal(2, analyst.UnreadCount);
        Assert.False(analyst.IsSquad);

        var channel = Assert.Single(summaries, summary => summary.Handle == "owner1:ops-squad");
        Assert.Equal("ops-squad", channel.DisplayName);
        Assert.Equal(1, channel.UnreadCount);
        Assert.True(channel.IsSquad);
    }

    [Fact]
    public async Task SurfaceWorkspaceMarkSeenClearsSingleAndAllTargets()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:ops-squad", SurfaceOrchestrationAgentTypes.SquadOrchestrator));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");

        RaiseChat(context, "owner1:analyst", "Analyst note", "analyst-1");
        RaiseChat(context, "owner1:ops-squad", "Squad update", "squad-1");

        workspace.MarkAgentSeen("analyst");

        Assert.Equal(1, workspace.TotalUnreadCount);
        Assert.DoesNotContain(workspace.GetUnreadSummaries(), summary => summary.Handle == "owner1:analyst");
        Assert.Contains(workspace.GetUnreadSummaries(), summary => summary.Handle == "owner1:ops-squad");

        workspace.MarkAllSeen();

        Assert.Equal(0, workspace.TotalUnreadCount);
        Assert.Empty(workspace.GetUnreadSummaries());
    }

    [Fact]
    public async Task SurfaceWorkspaceSelectedTargetDoesNotAccrueUnread()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        using var activeView = workspace.ActivateSelectedTargetView();
        workspace.SelectAgent("owner1:analyst");

        RaiseChat(context, "owner1:analyst", "Visible selected note", "analyst-1");

        Assert.Equal(0, workspace.TotalUnreadCount);
        Assert.Empty(workspace.GetUnreadSummaries());
        Assert.Contains(workspace.GetTimelineForAgent("owner1:analyst"), item =>
            item.Kind == SurfaceTimelineItemKind.Agent && item.Text == "Visible selected note");
    }

    [Fact]
    public async Task SurfaceWorkspaceNotifyOnlySelectionDoesNotSuppressUnread()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        RaiseChat(context, "owner1:assistant", "Assistant update", "assistant-1");

        Assert.Equal(1, workspace.TotalUnreadCount);
        var summary = Assert.Single(workspace.GetUnreadSummaries());
        Assert.Equal("owner1:assistant", summary.Handle);
        Assert.Equal(1, summary.UnreadCount);
    }

    [Fact]
    public async Task SurfaceWorkspaceSelectAgentNormalizesBareHandles()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:squad-velo-travel-desk-itinerary-event", "TravelAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("squad-velo-travel-desk-itinerary-event");

        Assert.NotNull(workspace.SelectedAgent);
        Assert.Equal("owner1:squad-velo-travel-desk-itinerary-event", workspace.SelectedAgent!.Handle);
    }

    [Fact]
    public async Task SurfaceWorkspaceSelectSquadNormalizesBareHandles()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:ops-squad", SurfaceOrchestrationAgentTypes.SquadOrchestrator));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectSquad("ops-squad");

        Assert.NotNull(workspace.SelectedSquad);
        Assert.Equal("owner1:ops-squad", workspace.SelectedSquad!.OrchestratorHandle);
        Assert.Equal("owner1:ops-squad", workspace.SelectedAgent?.Handle);
    }

    [Fact]
    public async Task SurfaceWorkspaceUnreadCountsAreSharedAcrossScopesWithoutDuplication()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        await using var provider = CreateSurfaceServiceProvider(context, options =>
        {
            options.ShowRunningAgentsByDefault = true;
        });
        await using var commandScope = provider.CreateAsyncScope();
        await using var notifyScope = provider.CreateAsyncScope();
        var commandWorkspace = commandScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var notifyWorkspace = notifyScope.ServiceProvider.GetRequiredService<SurfaceWorkspaceService>();
        var principal = new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test");

        await commandWorkspace.InitializeAsync(principal);
        await notifyWorkspace.InitializeAsync(principal);
        using var activeView = commandWorkspace.ActivateSelectedTargetView();
        commandWorkspace.SelectAgent("owner1:assistant");

        RaiseChat(context, "owner1:analyst", "Analyst update", "analyst-1");

        Assert.Equal(1, commandWorkspace.TotalUnreadCount);
        Assert.Equal(1, notifyWorkspace.TotalUnreadCount);
        Assert.Single(commandWorkspace.GetUnreadSummaries(), summary => summary.Handle == "owner1:analyst");
        Assert.Single(notifyWorkspace.GetUnreadSummaries(), summary => summary.Handle == "owner1:analyst");
    }

    [Fact]
    public async Task SurfaceWorkspaceChatCanUseRequestResponseMode()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.ResponseMessage = new AgentMessage
        {
            FromHandle = "owner1:assistant",
            ToHandle = "owner1",
            MessageType = "chat",
            Message = "Hello from the agent"
        };
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            ShowRunningAgentsByDefault = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.RequestResponse
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        var request = Assert.Single(context.RequestMessages);
        Assert.Equal(MessageKind.Request, request.Kind);
        Assert.Equal("chat", request.MessageType);
        Assert.Equal("owner1:assistant", request.ToHandle);
        Assert.Empty(context.SentMessages);
        Assert.Contains(workspace.Timeline, item => item.Kind == SurfaceTimelineItemKind.Principal && item.Text == "hello");
        Assert.Contains(workspace.Timeline, item => item.Kind == SurfaceTimelineItemKind.Agent && item.Text == "Hello from the agent");
    }

    [Fact]
    public async Task SurfaceWorkspaceChatCanUseOneWayExpectationWithFireAndForgetDelivery()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            ShowRunningAgentsByDefault = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget,
            CommandCenterChatMessageKind = SurfaceChatMessageKind.OneWay
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("notify only");

        var sent = Assert.Single(context.SentMessages);
        Assert.Equal(MessageKind.OneWay, sent.Kind);
        Assert.Equal("notify only", sent.Message);
        Assert.Empty(context.RequestMessages);
    }

    [Fact]
    public async Task SurfaceWorkspaceChatSendsFileIdsOnAgentMessage()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            ShowRunningAgentsByDefault = true
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("review this", ["file-1", "file-2"]);

        var sent = Assert.Single(context.SentMessages);
        Assert.Equal(["file-1", "file-2"], sent.Files);
        Assert.Equal("review this", sent.Message);
    }

    [Fact]
    public async Task SurfaceFileUploadClientUsesHostApiUploadWithTtl()
    {
        var hostApiClient = new FakeHostApiClient { UploadedFileId = "file-123" };
        var client = new SurfaceFileUploadClient(
            hostApiClient,
            NullLogger<SurfaceFileUploadClient>.Instance);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var uploaded = await client.UploadAsync(stream, "note.txt", "text/plain", TimeSpan.FromMinutes(10));

        Assert.Equal("file-123", uploaded.FileId);
        Assert.Equal("note.txt", uploaded.FileName);
        Assert.Equal("note.txt", hostApiClient.UploadedFileName);
        Assert.Equal(600, hostApiClient.UploadedTtlSeconds);
    }

    [Fact]
    public async Task SurfaceFileUploadClientUsesHostApiDelete()
    {
        var hostApiClient = new FakeHostApiClient();
        var client = new SurfaceFileUploadClient(
            hostApiClient,
            NullLogger<SurfaceFileUploadClient>.Instance);

        var deleted = await client.DeleteAsync("file-123");

        Assert.True(deleted);
        Assert.Equal("file-123", hostApiClient.DeletedFileId);
    }

    [Fact]
    public async Task SurfaceWorkspaceChatAppendsErrorWhenRequestResponseFails()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.SendAndReceiveException = new TimeoutException("timed out");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            ShowRunningAgentsByDefault = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.RequestResponse
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.SendChatAsync("hello");

        Assert.Single(context.RequestMessages);
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.AgentHandle == "owner1:assistant"
            && item.Text!.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SurfaceAgentListMergesTrackedAndSharedAgents()
    {
        var tracked = new List<TrackedAgentInfo>
        {
            new("owner1:assistant", "AssistantAgent")
            {
                Health = new AgentHealthStatus
                {
                    Handle = "owner1:assistant",
                    State = HealthState.Healthy,
                    Timestamp = DateTime.UtcNow,
                    IsConfigured = true
                }
            }
        };
        var shared = new List<AgentInfo>
        {
            new("owner2:research", "ResearchAgent", "research", AgentStatus.Active, DateTime.UtcNow, null, null),
            new("owner1:assistant", "AssistantAgent", "assistant", AgentStatus.Active, DateTime.UtcNow, null, null)
        };

        var agents = SurfaceAgentList.Merge("owner1", tracked, shared);

        Assert.Equal(2, agents.Count);
        Assert.Contains(agents, a => a.Handle == "owner1:assistant" && !a.IsShared && a.DisplayName == "assistant");
        Assert.Contains(agents, a => a.Handle == "owner2:research" && a.IsShared && a.DisplayName == "research");
    }

    [Fact]
    public void SurfaceAgentListHidesConfiguredAgentTypesByDefault()
    {
        var tracked = new List<TrackedAgentInfo>
        {
            new("owner1:assistant", "AssistantAgent"),
            new("owner1:surface", "surface")
        };

        var visible = SurfaceAgentList.Merge(
            "owner1",
            tracked,
            [],
            hiddenAgentTypes: ["surface"],
            hiddenAgentHandles: ["surface"]);
        var withHidden = SurfaceAgentList.Merge(
            "owner1",
            tracked,
            [],
            hiddenAgentTypes: ["surface"],
            hiddenAgentHandles: ["surface"],
            includeHidden: true);

        Assert.DoesNotContain(visible, a => a.Handle == "owner1:surface");
        var hidden = Assert.Single(withHidden, a => a.Handle == "owner1:surface");
        Assert.True(hidden.IsHidden);
    }

    [Fact]
    public void SurfaceAgentListMatchesBareAndOwnerQualifiedSurfaceAgentHandles()
    {
        var tracked = new List<TrackedAgentInfo>
        {
            new("owner1:crm-agent", "CrmAgent"),
            new("owner1:analyst", "AnalystAgent")
        };

        var agents = SurfaceAgentList.Merge(
            "owner1",
            tracked,
            [],
            surfaceAgentHandles: ["crm-agent", "owner1:analyst"]);

        Assert.Contains(agents, agent => agent.Handle == "owner1:crm-agent" && agent.IsSurfaceAgent);
        Assert.Contains(agents, agent => agent.Handle == "owner1:analyst" && agent.IsSurfaceAgent);
    }

    [Fact]
    public async Task SurfaceWorkspaceUsesDefaultSurfaceAgentsWhenPreferencesAreMissing()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        var options = new SurfaceOptions();
        options.DefaultSurfaceAgentHandles.Add("assistant");
        var workspace = CreateWorkspace(context, options);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        var agent = Assert.Single(workspace.Agents);
        Assert.Equal("owner1:assistant", agent.Handle);
        Assert.True(agent.IsSurfaceAgent);
    }

    [Fact]
    public async Task SurfaceWorkspaceDoesNotOverwriteStoredPreferencesWithDefaultSurfaceAgents()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var options = new SurfaceOptions();
        options.DefaultSurfaceAgentHandles.Add("assistant");
        var preferencesClient = new FakeSurfacePreferencesClient(new SurfacePreferences());
        var workspace = CreateWorkspace(context, options, preferencesClient: preferencesClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Empty(workspace.Agents);
        Assert.Contains(workspace.AllAgents, agent => agent.Handle == "owner1:assistant" && !agent.IsSurfaceAgent);
    }

    [Fact]
    public async Task SurfaceWorkspaceUsesRegistryHiddenAgentMetadata()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:hidden", "hidden-test-agent"));
        var options = new SurfaceOptions();
        options.HiddenAgentTypes.Clear();
        options.HiddenAgentHandles.Clear();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFabrCoreRegistry>(serviceProvider =>
            new FabrCoreRegistry(
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FabrCoreRegistry>>()));
        await using var provider = services.BuildServiceProvider();
        var workspace = CreateWorkspace(context, options, provider);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Empty(workspace.Agents);
        Assert.Single(workspace.AllAgents);

        await workspace.SetShowRunningAgentsAsync(true);
        Assert.Empty(workspace.Agents);
        await workspace.SetShowHiddenAgentsAsync(true);

        var hidden = Assert.Single(workspace.Agents);
        Assert.Equal("owner1:hidden", hidden.Handle);
        Assert.True(hidden.IsHidden);
    }

    [Fact]
    public async Task SurfaceWorkspaceRefreshDoesNotActivateTrackedAgents()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableLiveStatus = true,
            ShowRunningAgentsByDefault = true
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.RefreshAgentsAsync();

        Assert.NotEmpty(context.GetTrackedAgentsActivateValues);
        Assert.All(context.GetTrackedAgentsActivateValues, activate => Assert.False(activate));
    }

    [Fact]
    public async Task SurfaceWorkspaceRefreshDoesNotLoadDetailedHealthForEveryAgent()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:task-ops", SurfaceTaskAgentTypes.TaskRunner));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.RefreshAgentsAsync();

        Assert.Empty(context.GetAgentHealthCalls);
    }

    [Fact]
    public async Task SurfaceChatLinkLifecycleAllowsHealthyExternalAgent()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.AgentHealthStatuses["owner1:assistant"] = HealthyAgent("owner1:assistant");

        var state = await SurfaceChatLinkLifecycle.ResolveAsync(
            context,
            "owner1:assistant",
            allowExternalAgent: true);

        Assert.False(state.IsTracked);
        Assert.True(state.IsReady);
        Assert.Contains(context.GetAgentHealthCalls, call => call.Handle == "owner1:assistant");
    }

    [Fact]
    public async Task SurfaceChatLinkLifecycleCanRequireManualCreateForUntrackedAgent()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.AgentHealthStatuses["owner1:assistant"] = HealthyAgent("owner1:assistant");

        var state = await SurfaceChatLinkLifecycle.ResolveAsync(
            context,
            "owner1:assistant",
            allowExternalAgent: false);

        Assert.False(state.IsTracked);
        Assert.False(state.IsReady);
        Assert.Null(state.Health);
        Assert.Empty(context.GetAgentHealthCalls);
    }

    [Fact]
    public async Task SurfaceChatLinkLifecycleStillChecksTrackedAgentHealth()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent")
        {
            Health = HealthyAgent("owner1:assistant")
        });

        var state = await SurfaceChatLinkLifecycle.ResolveAsync(
            context,
            "owner1:assistant",
            allowExternalAgent: false);

        Assert.True(state.IsTracked);
        Assert.True(state.IsReady);
        Assert.Contains(context.GetAgentHealthCalls, call => call.Handle == "owner1:assistant");
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateAgentPinsCreatedAgentAsSurfaceAgent()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var preferencesClient = new FakeSurfacePreferencesClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            preferencesClient: preferencesClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateAgentAsync(new AgentConfiguration
        {
            Handle = "assistant",
            AgentType = "assistant-agent"
        });

        Assert.Contains("owner1:assistant", preferencesClient.Preferences!.SurfaceAgentHandles);
        var agent = Assert.Single(workspace.Agents);
        Assert.True(agent.IsSurfaceAgent);
    }

    [Fact]
    public async Task SurfaceWorkspaceSetSurfaceAgentPinsNormalizesAndUnpinsAgents()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var preferencesClient = new FakeSurfacePreferencesClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions(),
            preferencesClient: preferencesClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Empty(workspace.Agents);
        Assert.Single(workspace.AllAgents);

        await workspace.SetSurfaceAgentAsync("assistant", true);

        Assert.Contains("owner1:assistant", preferencesClient.Preferences!.SurfaceAgentHandles);
        var pinned = Assert.Single(workspace.Agents);
        Assert.Equal("owner1:assistant", pinned.Handle);
        Assert.True(pinned.IsSurfaceAgent);

        await workspace.SetSurfaceAgentAsync("assistant", false);

        Assert.DoesNotContain("owner1:assistant", preferencesClient.Preferences!.SurfaceAgentHandles);
        Assert.Empty(workspace.Agents);
        Assert.Single(workspace.AllAgents);
        Assert.False(workspace.AllAgents.Single().IsSurfaceAgent);
    }

    [Fact]
    public async Task SurfaceWorkspaceFiltersCommandCenterToSurfaceAgentsUnlessRunningAgentsAreEnabled()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:hidden", "hidden-agent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:pinned-hidden", "hidden-agent"));
        var options = new SurfaceOptions();
        options.HiddenAgentTypes.Add("hidden-agent");
        var preferences = new SurfacePreferences
        {
            SurfaceAgentHandles = ["owner1:pinned-hidden"]
        };
        var workspace = CreateWorkspace(
            context,
            options,
            preferencesClient: new FakeSurfacePreferencesClient(preferences));

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Equal(3, workspace.AllAgents.Count);
        var pinned = Assert.Single(workspace.Agents);
        Assert.Equal("owner1:pinned-hidden", pinned.Handle);
        Assert.True(pinned.IsHidden);
        Assert.True(pinned.IsSurfaceAgent);

        await workspace.SetShowRunningAgentsAsync(true);

        Assert.Contains(workspace.Agents, agent => agent.Handle == "owner1:assistant");
        Assert.DoesNotContain(workspace.Agents, agent => agent.Handle == "owner1:hidden");

        await workspace.SetShowHiddenAgentsAsync(true);

        Assert.Contains(workspace.Agents, agent => agent.Handle == "owner1:hidden");
        Assert.Equal(3, workspace.Agents.Count);
    }

    [Fact]
    public async Task SurfaceWorkspaceFallsBackToOptionDefaultsWhenPreferencesCannotLoad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions
            {
                ShowHiddenAgentsByDefault = true,
                ShowRunningAgentsByDefault = true
            },
            preferencesClient: new FakeSurfacePreferencesClient(new TimeoutException("storage down")));

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.True(workspace.ShowHiddenAgents);
        Assert.True(workspace.ShowRunningAgents);
        Assert.Single(workspace.Agents);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateAgentFailsWhenDisabled()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.CreateAgentAsync(new AgentConfiguration
            {
                Handle = "assistant",
                AgentType = "assistant-agent"
            }));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateAgentRefreshesAndSelectsCreatedAgent()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var health = await workspace.CreateAgentAsync(new AgentConfiguration
        {
            Handle = "assistant",
            AgentType = "assistant-agent",
            Models = "default"
        });

        var created = Assert.Single(context.CreatedAgentConfigurations);
        Assert.Equal("owner1:assistant", created.Handle);
        Assert.Equal("owner1:assistant", health.Handle);
        Assert.Equal("owner1:assistant", workspace.SelectedAgent?.Handle);
        Assert.Contains(workspace.Agents, agent => agent.Handle == "owner1:assistant");
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateSquadDefaultsToOrchestratorRouter()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var result = await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            Name = "Ops Desk",
            OrchestratorModel = "default"
        });

        Assert.Equal(SurfaceSquadType.Orchestrator, result.Squad.SquadType);
        Assert.Equal("owner1:squad-ops-desk", result.Squad.OrchestratorHandle);
        Assert.Empty(result.Squad.Agents);

        var created = Assert.Single(context.CreatedAgentConfigurations);
        Assert.Equal(SurfaceOrchestrationAgentTypes.SquadOrchestrator, created.AgentType);
        Assert.True(created.Args.ContainsKey(SurfaceSquadArgs.SquadDefinition));

        var channel = Assert.Single(workspace.Squads);
        Assert.Equal("Ops Desk", channel.Name);
        Assert.Equal("owner1:squad-ops-desk", workspace.SelectedSquad?.OrchestratorHandle);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateOrchestratorSquadCreatesSingleRouter()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var result = await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk",
            OrchestratorModel = "default"
        });

        Assert.Equal(SurfaceSquadType.Orchestrator, result.Squad.SquadType);
        Assert.Equal("owner1:squad-ops-desk", result.Squad.OrchestratorHandle);
        Assert.Empty(result.Squad.Agents);

        var created = Assert.Single(context.CreatedAgentConfigurations);
        Assert.Equal(SurfaceOrchestrationAgentTypes.SquadOrchestrator, created.AgentType);
        Assert.Equal("owner1:squad-ops-desk", created.Handle);
        Assert.Equal(SurfaceSquadType.Orchestrator, workspace.SelectedSquad?.SquadType);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateTaskSquadCreatesSingleTaskRunner()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var result = await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            TaskOptions = new SurfaceTaskSquadOptions
            {
                WorkerModelName = "worker"
            },
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "executor",
                    AgentType = "assistant-agent",
                    Role = SurfaceSquadMemberRole.Executor
                },
                new SurfaceSquadAgentDefinition
                {
                    Name = "policy",
                    AgentType = "policy-sme",
                    Role = SurfaceSquadMemberRole.SubjectMatterExpert
                }
            ]
        });

        Assert.Equal(SurfaceSquadType.Task, result.Squad.SquadType);
        Assert.Equal("owner1:squad-ops-desk", result.Squad.OrchestratorHandle);
        Assert.Equal("owner1:squad-ops-desk-executor", result.Squad.Agents[0].Handle);
        Assert.Equal("owner1:squad-ops-desk-policy", result.Squad.Agents[1].Handle);
        Assert.Equal(SurfaceSquadMemberRole.SubjectMatterExpert, result.Squad.Agents[1].Role);

        Assert.Equal(3, context.CreatedAgentConfigurations.Count);
        var runner = Assert.Single(context.CreatedAgentConfigurations, config =>
            string.Equals(config.AgentType, SurfaceTaskAgentTypes.TaskRunner, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("owner1:squad-ops-desk", runner.Handle);
        Assert.Equal("worker", runner.Models);
        Assert.Contains("\"squadType\":\"task\"", runner.Args[SurfaceSquadArgs.SquadDefinition], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"role\":\"subjectMatterExpert\"", runner.Args[SurfaceSquadArgs.SquadDefinition], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SurfaceBlueprintProvisionerDelegatesCanonicalBlueprintToHost()
    {
        var blueprintClient = new FakeSurfaceBlueprintClient();
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var provisioner = new SurfaceBlueprintProvisioner(
            blueprintClient,
            NullLogger<SurfaceBlueprintProvisioner>.Instance,
            squadConfigClient);

        var result = await provisioner.ApplyAsync("owner1", new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Version = "v1",
            Agents =
            [
                new AgentConfiguration
                {
                    Handle = "assistant",
                    AgentType = "assistant-agent"
                }
            ],
            Squads =
            [
                new SurfaceSquadDefinition
                {
                    Name = "Ops Desk",
                    Agents =
                    [
                        new SurfaceSquadAgentDefinition
                        {
                            Name = "sme",
                            AgentType = "subject-matter-expert"
                        }
                    ]
                }
            ]
        });

        Assert.Empty(squadConfigClient.Squads);
        Assert.Equal(0, squadConfigClient.SaveCount);

        var request = Assert.Single(blueprintClient.AppliedRequests);
        Assert.Equal("owner1", request.PrincipalId);
        Assert.Equal("workspace", request.Request.Name);
        Assert.Equal("v1", request.Request.Version);
        Assert.Single(request.Request.Agents);
        Assert.Equal("assistant", request.Request.Agents[0].Handle);
        Assert.Equal("Ops Desk", Assert.Single(request.Request.Squads).Name);
        Assert.Equal(0, result.SquadsCreated);
        Assert.Equal(0, result.SquadsSkipped);
        Assert.Equal(3, result.AgentConfigurationsRequested);
    }

    [Fact]
    public async Task SurfaceBlueprintProvisionerPreservesLinkedAgentsInSquadsExtension()
    {
        var blueprintClient = new FakeSurfaceBlueprintClient();
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var provisioner = new SurfaceBlueprintProvisioner(
            blueprintClient,
            NullLogger<SurfaceBlueprintProvisioner>.Instance,
            squadConfigClient);

        var result = await provisioner.ApplyAsync("owner1", new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Agents =
            [
                new AgentConfiguration
                {
                    Handle = "assistant",
                    AgentType = "assistant-agent"
                }
            ],
            Squads =
            [
                new SurfaceSquadDefinition
                {
                    Name = "Ops Desk",
                    Agents =
                    [
                        new SurfaceSquadAgentDefinition
                        {
                            Handle = "assistant",
                            Name = "Assistant",
                            AgentType = "assistant-agent"
                        }
                    ]
                }
            ]
        });

        var request = Assert.Single(blueprintClient.AppliedRequests);
        Assert.Single(request.Request.Agents);
        var linked = Assert.Single(Assert.Single(request.Request.Squads).Agents);
        Assert.Equal("assistant", linked.Handle);
        Assert.Equal("Assistant", linked.Name);
        Assert.Equal(0, result.SquadsCreated);
        Assert.Equal(2, result.AgentConfigurationsRequested);
    }

    [Fact]
    public async Task SurfaceBlueprintProvisionerDoesNotUseLegacySquadStorage()
    {
        var existing = SurfaceSquadService.BuildSquad(
            "owner1",
            new SurfaceSquadDefinition { Name = "Ops Desk" });
        var blueprintClient = new FakeSurfaceBlueprintClient();
        var squadConfigClient = new FakeSurfaceSquadConfigClient([existing]);
        var provisioner = new SurfaceBlueprintProvisioner(
            blueprintClient,
            NullLogger<SurfaceBlueprintProvisioner>.Instance,
            squadConfigClient);

        var result = await provisioner.ApplyAsync("owner1", new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Squads = [new SurfaceSquadDefinition { Name = "Ops Desk" }]
        });

        Assert.Equal(0, squadConfigClient.SaveCount);
        Assert.Equal(0, result.SquadsCreated);
        Assert.Equal(0, result.SquadsSkipped);
        Assert.Equal(1, result.AgentConfigurationsRequested);
        Assert.Single(blueprintClient.AppliedRequests);
    }

    [Fact]
    public async Task SurfaceWorkspaceAppliesStoredBlueprintOnEveryInitialize()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var blueprintClient = new FakeSurfaceBlueprintClient
        {
            StoredBlueprint = new SurfaceBlueprintDocument
            {
                Name = "workspace",
                Agents =
                [
                    new AgentConfiguration
                    {
                        Handle = "assistant",
                        AgentType = "assistant-agent"
                    }
                ]
            }
        };
        var provisioner = new SurfaceBlueprintProvisioner(
            blueprintClient,
            NullLogger<SurfaceBlueprintProvisioner>.Instance,
            new FakeSurfaceSquadConfigClient());
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            blueprintProvisioner: provisioner);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Equal(2, blueprintClient.GetCount);
        Assert.Equal(2, blueprintClient.ApplyCount);
        Assert.All(blueprintClient.AppliedRequests, call => Assert.Equal("owner1", call.PrincipalId));
    }

    [Fact]
    public async Task SurfaceWorkspaceRefreshFromStorageAppliesBlueprintAndLoadsSquads()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var blueprintClient = new FakeSurfaceBlueprintClient();
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var provisioner = new SurfaceBlueprintProvisioner(
            blueprintClient,
            NullLogger<SurfaceBlueprintProvisioner>.Instance,
            squadConfigClient);
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            squadConfigClient: squadConfigClient,
            blueprintProvisioner: provisioner);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        Assert.Empty(workspace.Squads);

        blueprintClient.StoredBlueprint = new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Squads =
            [
                new SurfaceSquadDefinition
                {
                    Name = "Ops Desk",
                    Agents =
                    [
                        new SurfaceSquadAgentDefinition
                        {
                            Name = "sme",
                            AgentType = "subject-matter-expert"
                        }
                    ]
                }
            ]
        };

        await workspace.RefreshFromStorageAsync();

        Assert.Empty(workspace.Squads);
        Assert.Equal(0, squadConfigClient.SaveCount);
        Assert.Equal(2, blueprintClient.GetCount);
        Assert.Equal(1, blueprintClient.ApplyCount);
    }

    [Fact]
    public async Task SurfaceBlueprintClientReturnsNullWhenNoBlueprintsAreStored()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });
        var client = CreateBlueprintClient(handler);

        var blueprint = await client.GetAsync("owner1");

        Assert.Null(blueprint);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/fabrcoreapi/Blueprint", request.Uri, StringComparison.Ordinal);
        Assert.True(request.Headers.TryGetValue("x-user", out var userHeaders));
        Assert.Contains("owner1", userHeaders);
        Assert.True(request.Headers.TryGetValue("x-user-handle", out var principalHandleHeaders));
        Assert.Contains("owner1", principalHandleHeaders);
    }

    [Fact]
    public async Task SurfaceBlueprintClientSavesAndAppliesBlueprint()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "name": "workspace",
                  "version": "v1",
                  "totalRequested": 1,
                  "successCount": 1,
                  "failureCount": 0,
                  "results": [
                    {
                      "handle": "owner1:assistant",
                      "state": "Ready",
                      "isConfigured": true,
                      "message": "Agent ready"
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
        });
        var client = CreateBlueprintClient(handler);

        await client.SaveAsync("owner1", new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Version = "v1"
        });
        var result = await client.ApplyAsync("owner1", new SurfaceBlueprintDocument
        {
            Name = "workspace",
            Version = "v1",
            Agents =
            [
                new AgentConfiguration
                {
                    Handle = "assistant",
                    AgentType = "assistant-agent"
                }
            ]
        });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("\"name\":\"workspace\"", handler.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Contains("/fabrcoreapi/Agent/blueprint", handler.Requests[1].Uri);
        Assert.Contains("\"agents\"", handler.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.TotalRequested);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.AgentConfigurationsRequested);
        var health = Assert.Single(result.Results);
        Assert.Equal("owner1:assistant", health.Handle);
        Assert.Equal(HealthState.Healthy, health.State);
        Assert.True(health.IsConfigured);
    }

    [Fact]
    public async Task SurfaceSquadConfigClientScopesStorageRequestsToPrincipalHandle()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateSquadConfigClient(handler);

        var squads = await client.GetAsync("owner1");
        await client.SaveAsync("owner1", squads);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("/fabrcoreapi/Storage/surface/command-center/squads", request.Uri);
            Assert.True(request.Headers.TryGetValue("x-user", out var userHeaders));
            Assert.Contains("owner1", userHeaders);
            Assert.True(request.Headers.TryGetValue("x-user-handle", out var principalHandleHeaders));
            Assert.Contains("owner1", principalHandleHeaders);
        });
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Contains("\"squads\":[]", handler.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SurfaceBlueprintJsonSerializesSurfaceEnumsAsStringsAndRejectsIntegers()
    {
        var blueprint = new SurfaceBlueprintDocument
        {
            Name = "test",
            Squads =
            [
                new SurfaceSquadDefinition
                {
                    SquadType = SurfaceSquadType.Task,
                    Name = "Job",
                    Agents =
                    [
                        new SurfaceSquadAgentDefinition
                        {
                            Name = "data-intel",
                            AgentType = "data-intel-agent",
                            Role = SurfaceSquadMemberRole.SubjectMatterExpert
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(blueprint, SurfaceJson.Options);

        Assert.Contains("\"squadType\":\"task\"", json);
        Assert.Contains("\"role\":\"subjectMatterExpert\"", json);
        Assert.DoesNotContain("\"squadType\":2", json);
        Assert.DoesNotContain("\"role\":1", json);

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SurfaceBlueprintDocument>("""
            {
              "name": "test",
              "squads": [
                {
                  "squadType": 2,
                  "name": "Job",
                  "agents": [
                    { "name": "data-intel", "agentType": "data-intel-agent", "role": 1 }
                  ]
                }
              ]
            }
            """, SurfaceJson.Options));
        Assert.Contains("SurfaceSquadType", ex.Message);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateSquadPersistsChannelConfig()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            Name = "Ops Desk",
            Description = "Operations support",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "sme",
                    AgentType = "subject-matter-expert"
                }
            ]
        });

        var saved = Assert.Single(squadConfigClient.Squads);
        Assert.Equal("Ops Desk", saved.Name);
        Assert.Equal(SurfaceSquadType.Orchestrator, saved.SquadType);
        Assert.Equal("Operations support", saved.Description);
        Assert.Equal("owner1:squad-ops-desk", saved.OrchestratorHandle);
        Assert.Equal("owner1:squad-ops-desk-sme", Assert.Single(saved.Agents).Handle);
        Assert.Equal(1, squadConfigClient.SaveCount);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateOrchestratorSquadPersistsSquadType()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk"
        });

        var saved = Assert.Single(squadConfigClient.Squads);
        Assert.Equal(SurfaceSquadType.Orchestrator, saved.SquadType);
        Assert.Equal("owner1:squad-ops-desk", saved.OrchestratorHandle);
    }

    [Fact]
    public async Task SurfaceWorkspaceCreateTaskSquadPersistsRolesAndTaskOptions()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            TaskOptions = new SurfaceTaskSquadOptions
            {
                WorkerModelName = "planner",
                ClientAgentOverlay = "Use the runbook."
            },
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "policy",
                    AgentType = "policy-sme",
                    Role = SurfaceSquadMemberRole.SubjectMatterExpert
                }
            ]
        });

        var saved = Assert.Single(squadConfigClient.Squads);
        Assert.Equal(SurfaceSquadType.Task, saved.SquadType);
        Assert.Equal("planner", saved.TaskOptions.WorkerModelName);
        Assert.Equal("Use the runbook.", saved.TaskOptions.ClientAgentOverlay);
        Assert.Equal(SurfaceSquadMemberRole.SubjectMatterExpert, Assert.Single(saved.Agents).Role);
    }

    [Fact]
    public async Task SurfaceWorkspaceLoadsPersistedSquadsOnInitialize()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var squadConfigClient = new FakeSurfaceSquadConfigClient(
        [
            new SurfaceSquad
            {
                SquadType = SurfaceSquadType.Orchestrator,
                Name = "Ops Desk",
                Slug = "ops-desk",
                PrincipalHandle = "owner1",
                OrchestratorHandle = "owner1:squad-ops-desk",
                Description = "Operations support",
                Agents =
                [
                    new SurfaceSquadAgent
                    {
                        Name = "sme",
                        Handle = "owner1:assistant",
                        AgentType = "assistant-agent"
                    }
                ]
            }
        ]);
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        var channel = Assert.Single(workspace.Squads);
        Assert.Equal("Ops Desk", channel.Name);
        Assert.Equal(SurfaceSquadType.Orchestrator, channel.SquadType);
        Assert.Equal("Operations support", channel.Description);
        Assert.Equal("owner1:assistant", Assert.Single(channel.Agents).Handle);
        Assert.Equal("owner1:squad-ops-desk", workspace.SelectedAgent?.Handle);
    }

    [Fact]
    public async Task SurfaceWorkspaceLeavesPersistedTaskSquadShellWhenHealthRestoresConfiguration()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var channel = new SurfaceSquad
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            Slug = "ops-desk",
            PrincipalHandle = "owner1",
            OrchestratorHandle = "owner1:squad-ops-desk",
            TaskOptions = new SurfaceTaskSquadOptions
            {
                WorkerModelName = "worker-model"
            }
        };
        context.AgentConfigurations[channel.OrchestratorHandle] = new AgentConfiguration
        {
            Handle = channel.OrchestratorHandle,
            AgentType = SurfaceTaskAgentTypes.TaskRunner
        };
        var squadConfigClient = new FakeSurfaceSquadConfigClient([channel]);
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Contains(context.GetAgentHealthCalls, call =>
            call.Handle == channel.OrchestratorHandle
            && call.DetailLevel == HealthDetailLevel.Basic);
        Assert.Empty(context.CreatedAgentConfigurations);
        Assert.Equal(channel.OrchestratorHandle, workspace.SelectedAgent?.Handle);
    }

    [Fact]
    public async Task SurfaceWorkspaceReconfiguresPersistedTaskSquadShellWhenHostReportsNotConfigured()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var channel = new SurfaceSquad
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            Slug = "ops-desk",
            PrincipalHandle = "owner1",
            OrchestratorHandle = "owner1:squad-ops-desk",
            TaskOptions = new SurfaceTaskSquadOptions
            {
                WorkerModelName = "worker-model",
                PersonaPrompt = "Coordinate the work."
            }
        };
        var squadConfigClient = new FakeSurfaceSquadConfigClient([channel]);
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { ShowRunningAgentsByDefault = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));

        Assert.Contains(context.GetAgentHealthCalls, call =>
            call.Handle == channel.OrchestratorHandle
            && call.DetailLevel == HealthDetailLevel.Basic);
        var config = Assert.Single(context.CreatedAgentConfigurations);
        Assert.Equal(channel.OrchestratorHandle, config.Handle);
        Assert.Equal(SurfaceTaskAgentTypes.TaskRunner, config.AgentType);
        Assert.Equal("worker-model", config.Models);
        Assert.Equal("Coordinate the work.", config.SystemPrompt);
        Assert.True(config.ForceReconfigure);
        Assert.Contains(channel.OrchestratorHandle, config.Args[SurfaceSquadArgs.SquadDefinition]);
        Assert.Equal(channel.OrchestratorHandle, workspace.SelectedAgent?.Handle);
    }

    [Fact]
    public async Task SurfaceWorkspaceCanAddExistingAgentToSquad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "assistant-agent")
        {
            Health = new AgentHealthStatus
            {
                Handle = "owner1:assistant",
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = new AgentConfiguration
                {
                    Handle = "owner1:assistant",
                    AgentType = "assistant-agent",
                    Description = "Helpful assistant"
                }
            }
        });
        context.AgentConfigurations["owner1:assistant"] = context.TrackedAgents[0].Health!.Configuration!;
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition { Name = "Ops Desk" });
        context.CreatedAgentConfigurations.Clear();

        var updated = await workspace.AddExistingAgentToSelectedSquadAsync("owner1:assistant", "sme");

        var agent = Assert.Single(updated.Agents);
        Assert.Equal("sme", agent.Name);
        Assert.Equal("owner1:assistant", agent.Handle);
        Assert.Equal("assistant-agent", agent.AgentType);
        var config = Assert.Single(context.CreatedAgentConfigurations);
        Assert.True(config.ForceReconfigure);
        Assert.Contains("owner1:assistant", config.Args[SurfaceSquadArgs.SquadDefinition]);
    }

    [Fact]
    public async Task SurfaceWorkspaceCanAddExistingAgentToOrchestratorSquad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "assistant-agent")
        {
            Health = new AgentHealthStatus
            {
                Handle = "owner1:assistant",
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = new AgentConfiguration
                {
                    Handle = "owner1:assistant",
                    AgentType = "assistant-agent",
                    Description = "Helpful assistant"
                }
            }
        });
        context.AgentConfigurations["owner1:assistant"] = context.TrackedAgents[0].Health!.Configuration!;
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk"
        });
        context.CreatedAgentConfigurations.Clear();

        var updated = await workspace.AddExistingAgentToSelectedSquadAsync("owner1:assistant", "sme");

        Assert.Equal(SurfaceSquadType.Orchestrator, updated.SquadType);
        Assert.Equal("owner1:assistant", Assert.Single(updated.Agents).Handle);
        var config = Assert.Single(context.CreatedAgentConfigurations);
        Assert.Equal(SurfaceOrchestrationAgentTypes.SquadOrchestrator, config.AgentType);
        Assert.True(config.ForceReconfigure);
        Assert.Contains("owner1:assistant", config.Args[SurfaceSquadArgs.SquadDefinition]);
    }

    [Fact]
    public async Task SurfaceWorkspacePersistsUpdatedSquadAgents()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "assistant-agent")
        {
            Health = new AgentHealthStatus
            {
                Handle = "owner1:assistant",
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = new AgentConfiguration
                {
                    Handle = "owner1:assistant",
                    AgentType = "assistant-agent"
                }
            }
        });
        context.AgentConfigurations["owner1:assistant"] = context.TrackedAgents[0].Health!.Configuration!;
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition { Name = "Ops Desk" });
        await workspace.AddExistingAgentToSelectedSquadAsync("owner1:assistant", "sme");

        var saved = Assert.Single(squadConfigClient.Squads);
        Assert.Equal("owner1:assistant", Assert.Single(saved.Agents).Handle);
        Assert.Equal(2, squadConfigClient.SaveCount);
    }

    [Fact]
    public async Task SurfaceWorkspaceCanRemoveAgentFromSquad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "assistant-agent")
        {
            Health = new AgentHealthStatus
            {
                Handle = "owner1:assistant",
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = new AgentConfiguration
                {
                    Handle = "owner1:assistant",
                    AgentType = "assistant-agent"
                }
            }
        });
        context.AgentConfigurations["owner1:assistant"] = context.TrackedAgents[0].Health!.Configuration!;
        var squadConfigClient = new FakeSurfaceSquadConfigClient();
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true },
            squadConfigClient: squadConfigClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition { Name = "Ops Desk" });
        await workspace.AddExistingAgentToSelectedSquadAsync("owner1:assistant", "sme");
        context.CreatedAgentConfigurations.Clear();

        var updated = await workspace.RemoveAgentFromSelectedSquadAsync("owner1:assistant");

        Assert.Empty(updated.Agents);
        Assert.Empty(workspace.SelectedSquad?.Agents ?? []);
        Assert.Empty(Assert.Single(squadConfigClient.Squads).Agents);
        Assert.Equal(3, squadConfigClient.SaveCount);
        var config = Assert.Single(context.CreatedAgentConfigurations);
        Assert.True(config.ForceReconfigure);
        Assert.DoesNotContain("owner1:assistant", config.Args[SurfaceSquadArgs.SquadDefinition], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SurfaceWorkspaceCanCreateAgentForSquad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition { Name = "Ops Desk" });
        context.CreatedAgentConfigurations.Clear();

        var result = await workspace.CreateAgentForSelectedSquadAsync(new SurfaceSquadAgentDefinition
        {
            Name = "sme",
            AgentType = "subject-matter-expert",
            Models = "default",
            Plugins = ["Search"]
        });

        Assert.Equal("owner1:squad-ops-desk-sme", Assert.Single(result.Squad.Agents).Handle);
        Assert.Equal(2, context.CreatedAgentConfigurations.Count);
        var member = Assert.Single(context.CreatedAgentConfigurations, config =>
            string.Equals(config.AgentType, "subject-matter-expert", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("owner1:squad-ops-desk-sme", member.Handle);
        Assert.Contains("Search", member.Plugins);
        Assert.Equal("sme", member.Args[SurfaceSquadArgs.AgentName]);
    }

    [Fact]
    public async Task SurfaceWorkspaceCanCreateAgentForOrchestratorSquad()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk"
        });
        context.CreatedAgentConfigurations.Clear();

        var result = await workspace.CreateAgentForSelectedSquadAsync(new SurfaceSquadAgentDefinition
        {
            Name = "sme",
            AgentType = "subject-matter-expert",
            Models = "default",
            Plugins = ["Search"]
        });

        Assert.Equal(SurfaceSquadType.Orchestrator, result.Squad.SquadType);
        Assert.Equal("owner1:squad-ops-desk-sme", Assert.Single(result.Squad.Agents).Handle);
        Assert.Equal(2, context.CreatedAgentConfigurations.Count);
        Assert.Contains(context.CreatedAgentConfigurations, config =>
            string.Equals(config.AgentType, "subject-matter-expert", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(context.CreatedAgentConfigurations, config =>
            string.Equals(config.AgentType, SurfaceOrchestrationAgentTypes.SquadOrchestrator, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SurfaceWorkspaceSquadChatRoutesMentionsAndDefaultsToOrchestrator()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableAgentCreate = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            Name = "Ops Desk",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "sme",
                    AgentType = "subject-matter-expert"
                }
            ]
        });
        context.SentMessages.Clear();
        context.RequestMessages.Clear();

        await workspace.SendChatAsync("help me plan this");
        await workspace.SendChatAsync("@sme check the schedule");

        Assert.Equal("owner1:squad-ops-desk", context.RequestMessages[0].ToHandle);
        Assert.Equal("help me plan this", context.RequestMessages[0].Message);
        Assert.Equal("owner1:squad-ops-desk-sme", context.RequestMessages[1].ToHandle);
        Assert.Equal("check the schedule", context.RequestMessages[1].Message);
        Assert.All(context.RequestMessages, message =>
            Assert.Equal("owner1:squad-ops-desk", message.Args![SurfaceSquadArgs.SquadHandle]));
    }

    [Fact]
    public async Task SurfaceWorkspaceOrchestratorSquadRejectsPlannerMention()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableAgentCreate = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk"
        });
        context.SentMessages.Clear();

        await workspace.SendChatAsync("@planner make a plan");

        Assert.Empty(context.SentMessages);
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.Text!.Contains("@planner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SurfaceWorkspaceOrchestratorSquadRoutesDefaultChatToRouter()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableAgentCreate = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Ops Desk"
        });
        context.SentMessages.Clear();
        context.RequestMessages.Clear();

        await workspace.SendChatAsync("route this");

        var sent = Assert.Single(context.RequestMessages);
        Assert.Equal("owner1:squad-ops-desk", sent.ToHandle);
        Assert.Equal("route this", sent.Message);
        Assert.Equal("owner1:squad-ops-desk", sent.Args![SurfaceSquadArgs.SquadHandle]);
    }

    [Fact]
    public async Task SurfaceWorkspaceTaskSquadRoutesMentionsThroughRunner()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableAgentCreate = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "executor",
                    AgentType = "assistant-agent",
                    Role = SurfaceSquadMemberRole.Executor
                }
            ]
        });
        context.SentMessages.Clear();
        context.RequestMessages.Clear();

        await workspace.SendChatAsync("@executor finish the closeout");

        var sent = Assert.Single(context.RequestMessages);
        Assert.Equal("owner1:squad-ops-desk", sent.ToHandle);
        Assert.Equal("@executor finish the closeout", sent.Message);
        Assert.Equal("owner1:squad-ops-desk", sent.Args![SurfaceSquadArgs.SquadHandle]);
    }

    [Fact]
    public async Task SurfaceWorkspaceSquadDirectMentionResponseStaysInSquadTimeline()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions
        {
            EnableAgentCreate = true,
            CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget
        });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            Name = "Ops Desk",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "sme",
                    AgentType = "subject-matter-expert"
                }
            ]
        });
        context.SentMessages.Clear();
        context.RequestMessages.Clear();

        await workspace.SendChatAsync("@sme check the schedule");

        var request = Assert.Single(context.RequestMessages);
        context.Raise(new AgentMessage
        {
            FromHandle = request.ToHandle,
            ToHandle = request.FromHandle,
            Channel = request.Channel,
            State = request.State is null ? null : new Dictionary<string, string>(request.State),
            MessageType = "chat",
            Message = "The schedule is clear."
        });

        Assert.Contains(workspace.GetTimelineForAgent("owner1:squad-ops-desk"), item =>
            item.Kind == SurfaceTimelineItemKind.Agent
            && item.AgentHandle == "owner1:squad-ops-desk"
            && item.Text == "The schedule is clear.");
    }

    [Fact]
    public async Task SurfaceWorkspaceSquadChatRejectsUnknownMention()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = CreateWorkspace(context, new SurfaceOptions { EnableAgentCreate = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            Name = "Ops Desk",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "sme",
                    AgentType = "subject-matter-expert"
                }
            ]
        });
        context.SentMessages.Clear();

        await workspace.SendChatAsync("@missing hello");

        Assert.Empty(context.SentMessages);
        Assert.Contains(workspace.Timeline, item =>
            item.Kind == SurfaceTimelineItemKind.Error
            && item.Text!.Contains("@missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SurfaceMessageClassifierUsesSquadHandleForTimelineGrouping()
    {
        var item = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:squad-ops-desk-planner",
            ToHandle = "owner1",
            MessageType = SurfaceSquadMessageTypes.AgentResponse,
            Message = "Plan ready",
            Args = new Dictionary<string, string>
            {
                [SurfaceSquadArgs.SquadHandle] = "owner1:squad-ops-desk"
            }
        });

        Assert.Equal("owner1:squad-ops-desk", item.AgentHandle);
        Assert.Equal("owner1:squad-ops-desk-planner", item.Author);
        Assert.True(item.DisplayInChat);
    }

    [Fact]
    public void SurfaceMessageClassifierUsesSquadStateForTimelineGrouping()
    {
        var item = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:squad-ops-desk-sme",
            ToHandle = "owner1",
            MessageType = "chat",
            Message = "Done",
            State = new Dictionary<string, string>
            {
                [SurfaceSquadArgs.SquadHandle] = "owner1:squad-ops-desk"
            }
        });

        Assert.Equal("owner1:squad-ops-desk", item.AgentHandle);
        Assert.Equal("owner1:squad-ops-desk-sme", item.Author);
        Assert.True(item.DisplayInChat);
    }

    [Fact]
    public void SurfaceMessageClassifierFallsBackToHandleShapedMessageChannelForTimelineGrouping()
    {
        var item = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:squad-ops-desk-sme",
            ToHandle = "owner1",
            Channel = "owner1:squad-ops-desk",
            MessageType = "chat",
            Message = "Done"
        });

        Assert.Equal("owner1:squad-ops-desk", item.AgentHandle);
    }

    [Fact]
    public void SurfaceMarkdownRendererFormatsChatMarkdown()
    {
        var html = SurfaceMarkdownRenderer.Render("""
            ## Customers

            | Name | Status |
            |---|---|
            | **Northwind** | Active |

            - Contact
              - Priya Raman
            """);

        Assert.Contains("<h2", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<strong>Northwind</strong>", html);
        Assert.Contains("<ul>", html);
    }

    [Fact]
    public void SurfaceMarkdownRendererDisablesUnsafeHtmlAndLinks()
    {
        var html = SurfaceMarkdownRenderer.Render("""
            <script>alert('x')</script>

            [bad](javascript:alert(1))
            """);

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public async Task SurfaceSquadConversationBusMirrorsOriginalMessageRoute()
    {
        var host = new FakeAgentHost("owner1:squad-ops-desk");
        var bus = new SurfaceSquadConversationBus(
            host,
            new SurfaceSquadRuntime
            {
                Squad = new SurfaceSquad
                {
                    Name = "Ops Desk",
                    Slug = "ops-desk",
                    PrincipalHandle = "owner1",
                    OrchestratorHandle = "owner1:squad-ops-desk"
                }
            });

        await bus.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = "owner1:squad-ops-desk",
            ToHandle = "owner1:crm-agent",
            Message = "Get customers"
        });

        Assert.Collection(host.SentMessages,
            requestMirror =>
            {
                Assert.Equal(SurfaceSquadMessageTypes.AgentRequest, requestMirror.MessageType);
                Assert.Equal("true", requestMirror.Args![SurfaceSquadArgs.Mirror]);
                Assert.Equal("owner1:squad-ops-desk", requestMirror.Args[SurfaceSquadArgs.OriginalFromHandle]);
                Assert.Equal("owner1:crm-agent", requestMirror.Args[SurfaceSquadArgs.OriginalToHandle]);
            },
            responseMirror =>
            {
                Assert.Equal(SurfaceSquadMessageTypes.AgentResponse, responseMirror.MessageType);
                Assert.Equal("true", responseMirror.Args![SurfaceSquadArgs.Mirror]);
                Assert.Equal("owner1:crm-agent", responseMirror.Args[SurfaceSquadArgs.OriginalFromHandle]);
                Assert.Equal("owner1:squad-ops-desk", responseMirror.Args[SurfaceSquadArgs.OriginalToHandle]);
            });
    }

    [Fact]
    public void SurfaceTaskHarnessProjectsSquadMembersIntoNamedDelegates()
    {
        var squad = TaskHarnessSquad(
            new SurfaceSquadAgent
            {
                Name = "Customer Records",
                Handle = "owner1:crm",
                AgentType = "crm-agent",
                Role = SurfaceSquadMemberRole.Executor
            },
            new SurfaceSquadAgent
            {
                Name = "Policy Desk",
                Handle = "owner1:policy",
                AgentType = "policy-sme",
                Role = SurfaceSquadMemberRole.SubjectMatterExpert
            },
            new SurfaceSquadAgent
            {
                Name = "Offline Helper",
                Handle = "owner1:offline",
                AgentType = "helper-agent",
                Role = SurfaceSquadMemberRole.Executor
            });

        var capabilities = new List<SurfaceSquadAgentCapability>
        {
            new() { Name = "Customer Records", Handle = "owner1:crm", Description = "Owns CRM customers.", IsConfigured = true },
            new() { Name = "Policy Desk", Handle = "owner1:policy", Description = "Knows the runbook.", IsConfigured = true },
            new() { Name = "Offline Helper", Handle = "owner1:offline", Description = "Agent Offline Helper", UnavailableReason = "Agent is not configured." }
        };

        var host = new FakeAgentHost(squad.OrchestratorHandle);
        var bus = new SurfaceSquadConversationBus(host, new SurfaceSquadRuntime { Squad = squad });

        var delegates = SurfaceSquadMemberAgent.BuildAgents(
            squad,
            capabilities,
            bus,
            host,
            clientAgentOverlay: null,
            TimeSpan.FromSeconds(30),
            out var excluded);

        Assert.Equal(["Customer Records", "Policy Desk"], delegates.Select(d => d.Name ?? string.Empty).ToArray());
        Assert.Contains("Owns CRM customers.", delegates[0].Description);
        Assert.Contains("Role: Executor", delegates[0].Description);
        Assert.Contains("Role: Subject matter expert", delegates[1].Description);
        Assert.Contains("advisory only", delegates[1].Description);
        Assert.Contains("Offline Helper", Assert.Single(excluded));
    }

    [Fact]
    public void SurfaceTaskHarnessResolvesUniqueNonEmptyDelegateNames()
    {
        // BackgroundAgentsProvider throws on blank or case-insensitively duplicate names.
        var squad = TaskHarnessSquad(
            new SurfaceSquadAgent { Name = "Analyst", Handle = "owner1:a1", AgentType = "x", Role = SurfaceSquadMemberRole.Executor },
            new SurfaceSquadAgent { Name = "analyst", Handle = "owner1:a2", AgentType = "x", Role = SurfaceSquadMemberRole.Executor },
            new SurfaceSquadAgent { Name = "   ", Handle = "owner1:fallback-alias", AgentType = "x", Role = SurfaceSquadMemberRole.Executor });

        var host = new FakeAgentHost(squad.OrchestratorHandle);
        var bus = new SurfaceSquadConversationBus(host, new SurfaceSquadRuntime { Squad = squad });

        var delegates = SurfaceSquadMemberAgent.BuildAgents(
            squad, [], bus, host, clientAgentOverlay: null, TimeSpan.FromSeconds(30), out _);

        Assert.Equal(["Analyst", "analyst-2", "fallback-alias"], delegates.Select(d => d.Name ?? string.Empty).ToArray());
        Assert.All(delegates, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));

        // The real provider must accept this roster.
        _ = new BackgroundAgentsProvider(delegates);
    }

    [Fact]
    public async Task SurfaceTaskHarnessDelegationUsesTaskDelegationOverTheSquadBus()
    {
        var squad = TaskHarnessSquad(new SurfaceSquadAgent
        {
            Name = "Customer Records",
            Handle = "owner1:crm",
            AgentType = "crm-agent",
            Role = SurfaceSquadMemberRole.Executor
        });

        var host = new FakeAgentHost(squad.OrchestratorHandle);
        host.Responders["owner1:crm"] = _ => "Found 3 customers.";
        var bus = new SurfaceSquadConversationBus(host, new SurfaceSquadRuntime { Squad = squad });

        var delegates = SurfaceSquadMemberAgent.BuildAgents(
            squad, [], bus, host, clientAgentOverlay: "Use the runbook.", TimeSpan.FromSeconds(30), out _);

        var response = await delegates[0].RunAsync("List active customers.");

        Assert.Equal("Found 3 customers.", response.Text);

        var request = Assert.Single(host.ReceivedRequests);
        Assert.Equal("owner1:crm", request.ToHandle);
        Assert.Equal(SurfaceSquadMessageTypes.TaskDelegation, request.MessageType);
        Assert.Equal(MessageKind.Request, request.Kind);
        Assert.StartsWith("Use the runbook.", request.Message);
        Assert.Contains("List active customers.", request.Message);
        Assert.Equal("Customer Records", request.State![SurfaceSquadArgs.AgentName]);
        Assert.Equal(squad.OrchestratorHandle, request.State[SurfaceSquadArgs.SquadHandle]);

        // Both legs are mirrored onto the principal's timeline.
        var mirrors = host.SentMessages.Where(m => m.Args?.ContainsKey(SurfaceSquadArgs.Mirror) == true).ToList();
        Assert.Collection(
            mirrors,
            requestMirror => Assert.Equal(SurfaceSquadMessageTypes.AgentRequest, requestMirror.MessageType),
            responseMirror => Assert.Equal(SurfaceSquadMessageTypes.AgentResponse, responseMirror.MessageType));
        Assert.All(mirrors, m => Assert.Equal("owner1", m.ToHandle));
    }

    [Fact]
    public async Task SurfaceTaskHarnessDelegationFailsWhenTheMemberExceedsTheTimeout()
    {
        var squad = TaskHarnessSquad(new SurfaceSquadAgent
        {
            Name = "Slow Agent",
            Handle = "owner1:slow",
            AgentType = "slow-agent",
            Role = SurfaceSquadMemberRole.Executor
        });

        var host = new FakeAgentHost(squad.OrchestratorHandle);
        host.Delays["owner1:slow"] = TimeSpan.FromSeconds(30);
        var bus = new SurfaceSquadConversationBus(host, new SurfaceSquadRuntime { Squad = squad });

        var delegates = SurfaceSquadMemberAgent.BuildAgents(
            squad, [], bus, host, clientAgentOverlay: null, TimeSpan.FromMilliseconds(50), out _);

        await Assert.ThrowsAsync<TimeoutException>(() => delegates[0].RunAsync("Do the thing"));
    }

    [Fact]
    public async Task SurfaceTaskHarnessExposesTodoAndBackgroundProvidersThroughGetService()
    {
        // TodoCompletionLoopEvaluator and BackgroundTaskCompletionLoopEvaluator resolve their providers
        // via AIAgent.GetService through the whole decorator chain. If this breaks, the loop never terminates.
        var (agent, _) = await CreateTaskHarnessAgentAsync(FakeChatClient.WithTextResponse("done"));

        await agent.OnInitialize();

        Assert.NotNull(agent.HarnessAgent);
        Assert.NotNull(agent.HarnessAgent!.GetService<TodoProvider>());
        Assert.NotNull(agent.HarnessAgent.GetService<BackgroundAgentsProvider>());
    }

    [Fact]
    public async Task SurfaceTaskHarnessRunsTodosAndDelegationsToCompletion()
    {
        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Pull the customer list"}]}"""),
            FakeChatClient.ToolCall("c2", "background_agents_start_task", """{"agentName":"Customer Records","input":"List active customers.","description":"Pull customers"}"""),
            FakeChatClient.Text("Delegated the fetch."),
            FakeChatClient.ToolCall("c3", "todos_complete", """{"items":[{"id":1,"reason":"Customer list returned"}]}"""),
            FakeChatClient.Text("There are 3 active customers."));

        var (agent, host) = await CreateTaskHarnessAgentAsync(chatClient);
        host.Responders["owner1:crm"] = _ => "Found 3 customers.";

        await agent.OnInitialize();
        var response = await agent.OnMessage(new AgentMessage
        {
            FromHandle = "owner1",
            ToHandle = "owner1:squad-ops-desk",
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Request,
            Message = "How many active customers do we have?"
        });

        Assert.Equal("There are 3 active customers.", response.Message);

        // The loop terminated on its own — no timer trampoline, and every todo was closed.
        Assert.Empty(host.RegisteredTimers);
        Assert.Empty(await agent.HarnessAgent!.GetService<TodoProvider>()!.GetRemainingTodosAsync(agent.HarnessSession!));

        // The delegation actually reached the member agent.
        var delegation = Assert.Single(host.ReceivedRequests);
        Assert.Equal("owner1:crm", delegation.ToHandle);
        Assert.Equal(SurfaceSquadMessageTypes.TaskDelegation, delegation.MessageType);

        // The consolidated answer is mirrored to the principal.
        Assert.Contains(host.SentMessages, m =>
            m.ToHandle == "owner1"
            && m.MessageType == SurfaceSquadMessageTypes.Chat
            && m.Message == "There are 3 active customers.");
    }

    [Fact]
    public async Task SurfaceTaskHarnessReportsUnfinishedTodosWhenTheIterationBudgetRunsOut()
    {
        // The model adds a todo and never completes it. The loop must stop at MaxLoopIterations
        // and say so, rather than reporting success.
        var chatClient = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("c1", "todos_add", """{"todos":[{"title":"Never finished"}]}"""),
            FakeChatClient.Text("Working on it."));

        var (agent, _) = await CreateTaskHarnessAgentAsync(chatClient, maxLoopIterations: 2);

        await agent.OnInitialize();
        var response = await agent.OnMessage(new AgentMessage
        {
            FromHandle = "owner1",
            ToHandle = "owner1:squad-ops-desk",
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Request,
            Message = "Do the thing"
        });

        Assert.Contains("were not completed within the iteration budget", response.Message);
        Assert.Contains("Never finished", response.Message);
    }

    [Fact]
    public async Task SurfaceTaskHarnessSendsSquadPersonaAndDelegateRosterToTheModel()
    {
        // The retired runner injected PersonaPrompt into the planner prompt only, and never applied
        // config.SystemPrompt to any LLM call at all. Both must reach the coordinator now.
        var squad = TaskHarnessSquad(
            new SurfaceSquadAgent { Name = "Customer Records", Handle = "owner1:crm", AgentType = "crm-agent", Role = SurfaceSquadMemberRole.Executor },
            new SurfaceSquadAgent { Name = "Policy Desk", Handle = "owner1:policy", AgentType = "policy-sme", Role = SurfaceSquadMemberRole.SubjectMatterExpert });
        squad.TaskOptions.PersonaPrompt = "Always cite the runbook.";

        var chatClient = FakeChatClient.WithTextResponse("done");
        var (agent, _) = await CreateTaskHarnessAgentAsync(chatClient, squad);

        await agent.OnInitialize();
        await agent.OnMessage(new AgentMessage
        {
            FromHandle = "owner1",
            ToHandle = squad.OrchestratorHandle,
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Request,
            Message = "Do the thing"
        });

        var prompt = string.Join(
            "\n",
            [chatClient.RequestOptions[0]?.Instructions ?? string.Empty, .. chatClient.Requests[0].Select(m => m.Text)]);

        Assert.Contains("Always cite the runbook.", prompt);
        Assert.Contains("Ops Desk", prompt);

        // Provider instructions and the registry-derived roster are both present.
        Assert.Contains("todos_add", prompt);
        Assert.Contains("background_agents_", prompt);
        Assert.Contains("Customer Records", prompt);
        Assert.Contains("Policy Desk", prompt);
        Assert.Contains("advisory only", prompt);
    }

    [Fact]
    public async Task SurfaceTaskHarnessDeclinesGoalsWhenTheSquadHasNoUsableMembers()
    {
        var squad = TaskHarnessSquad();
        var (agent, _) = await CreateTaskHarnessAgentAsync(FakeChatClient.WithTextResponse("done"), squad: squad);

        await agent.OnInitialize();
        var response = await agent.OnMessage(new AgentMessage
        {
            FromHandle = "owner1",
            ToHandle = squad.OrchestratorHandle,
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Request,
            Message = "Do the thing"
        });

        Assert.Null(agent.HarnessAgent);
        Assert.Contains("Add at least one executor agent", response.Message);
    }

    private static SurfaceSquad TaskHarnessSquad(params SurfaceSquadAgent[] agents)
        => new()
        {
            SquadType = SurfaceSquadType.Task,
            Name = "Ops Desk",
            Slug = "ops-desk",
            PrincipalHandle = "owner1",
            OrchestratorHandle = "owner1:squad-ops-desk",
            Agents = [.. agents]
        };

    private static Task<(SurfaceTaskHarnessAgent Agent, FakeAgentHost Host)> CreateTaskHarnessAgentAsync(
        FakeChatClient chatClient,
        SurfaceSquad? squad = null,
        int maxLoopIterations = 10)
    {
        squad ??= TaskHarnessSquad(new SurfaceSquadAgent
        {
            Name = "Customer Records",
            Handle = "owner1:crm",
            AgentType = "crm-agent",
            Role = SurfaceSquadMemberRole.Executor
        });
        squad.TaskOptions.MaxLoopIterations = maxLoopIterations;

        var config = new AgentConfiguration
        {
            Handle = squad.OrchestratorHandle,
            AgentType = SurfaceTaskAgentTypes.TaskRunner,
            Models = "default",
            Args = new Dictionary<string, string>
            {
                [SurfaceSquadArgs.SquadDefinition] = SurfaceSquadRuntime.Serialize(new SurfaceSquadRuntime { Squad = squad })
            }
        };

        var host = new FakeAgentHost(squad.OrchestratorHandle);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IFabrCoreChatClientService>(new FakeChatClientService(chatClient));
        var provider = services.BuildServiceProvider();

        return Task.FromResult((new SurfaceTaskHarnessAgent(config, provider, host), host));
    }

    [Fact]
    public async Task SurfaceWorkspaceDiscoveryFailureDoesNotBreakAgentList()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var discoveryClient = new FakeSurfaceDiscoveryClient(new TimeoutException("timed out"));
        var workspace = CreateWorkspace(
            context,
            new SurfaceOptions { EnableAgentCreate = true, ShowRunningAgentsByDefault = true },
            discoveryClient: discoveryClient);

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var discovery = await workspace.LoadDiscoveryAsync();

        Assert.Null(discovery);
        Assert.Contains("timed out", workspace.DiscoveryError);
        Assert.Contains(workspace.Agents, agent => agent.Handle == "owner1:assistant");
    }

    [Fact]
    public async Task SurfaceWorkspaceInitializeIsIdempotentForSameOwner()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });
        var principal = new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test");

        await workspace.InitializeAsync(principal);
        await workspace.InitializeAsync(principal);

        context.Raise(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            ToHandle = "owner1",
            Message = "Hello",
            MessageType = "chat"
        });

        Assert.False(context.IsDisposed);
        Assert.Single(workspace.Timeline);
    }

    [Fact]
    public void SurfaceAgentConfigurationDraftBuildsFullAgentConfiguration()
    {
        var draft = new SurfaceAgentConfigurationDraft
        {
            Handle = "assistant",
            AgentType = "assistant-agent",
            Models = "default",
            Description = "Demo assistant",
            SystemPrompt = "Be helpful.",
            PluginAliases = "GitHub\nSearch",
            ToolAliases = "GetTime, JsonFormat",
            Streams = "alerts",
            ForceReconfigure = true
        };
        draft.Args[0].Key = "GitHub:Organization";
        draft.Args[0].Value = "fabrcore";
        var mcp = new SurfaceMcpServerDraft
        {
            Name = "filesystem",
            TransportType = McpTransportType.Stdio,
            Command = "npx",
            Arguments = "-y\n@modelcontextprotocol/server-filesystem"
        };
        mcp.Env[0].Key = "ROOT";
        mcp.Env[0].Value = "C:\\tmp";
        draft.McpServers.Add(mcp);

        var config = draft.Build();

        Assert.Equal("assistant", config.Handle);
        Assert.Equal("assistant-agent", config.AgentType);
        Assert.Equal(["GitHub", "Search"], config.Plugins);
        Assert.Equal(["GetTime", "JsonFormat"], config.Tools);
        var stream = Assert.Single(config.Streams);
        Assert.Equal("alerts", stream.Namespace);
        Assert.Equal("alerts", stream.Channel);
        Assert.Equal("fabrcore", config.Args["GitHub:Organization"]);
        Assert.True(config.ForceReconfigure);
        var server = Assert.Single(config.McpServers);
        Assert.Equal("filesystem", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal("C:\\tmp", server.Env["ROOT"]);
    }

    [Fact]
    public void SurfaceMessageClassifierClassifiesSystemAndChatMessages()
    {
        var status = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = SystemMessageTypes.Status,
            Message = "Thinking"
        });
        var error = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = SystemMessageTypes.Error,
            Message = "Failed"
        });
        var chat = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = "chat",
            Message = "Done"
        });
        var hiddenChat = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = "chat",
            Message = "_internal note"
        });

        Assert.Equal(SurfaceTimelineItemKind.Status, status.Kind);
        Assert.True(status.IsSystemMessage);
        Assert.False(status.DisplayInChat);
        Assert.Equal(SurfaceTimelineItemKind.Error, error.Kind);
        Assert.True(error.IsSystemMessage);
        Assert.True(error.DisplayInChat);
        Assert.Equal(SurfaceTimelineItemKind.Agent, chat.Kind);
        Assert.False(chat.IsSystemMessage);
        Assert.True(chat.DisplayInChat);
        Assert.Equal(SurfaceTimelineItemKind.Agent, hiddenChat.Kind);
        Assert.False(hiddenChat.IsSystemMessage);
        Assert.False(hiddenChat.DisplayInChat);
    }

    [Fact]
    public void SurfaceMessageClassifierTreatsUnderscoreMessagesAsSystemControlMessages()
    {
        var thinking = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = SystemMessageTypes.Thinking,
            Message = "Reading the account notes"
        });
        var unknownSystem = SurfaceMessageClassifier.Classify(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            MessageType = "_custom_control",
            Message = "Custom progress text"
        });

        Assert.Equal(SurfaceTimelineItemKind.Status, thinking.Kind);
        Assert.True(thinking.IsSystemMessage);
        Assert.False(thinking.DisplayInChat);
        Assert.Equal("Reading the account notes", thinking.Text);
        Assert.Equal(SurfaceTimelineItemKind.Status, unknownSystem.Kind);
        Assert.True(unknownSystem.IsSystemMessage);
        Assert.False(unknownSystem.DisplayInChat);
        Assert.Equal("Custom progress text", unknownSystem.Text);
    }

    [Fact]
    public async Task SurfaceWorkspaceUsesSystemMessagesAsActivityNotChatBubbles()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");
        context.Raise(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            ToHandle = "owner1",
            MessageType = SystemMessageTypes.Thinking,
            Message = "Looking up invoice history"
        });

        var statusItem = Assert.Single(workspace.Timeline);
        Assert.Equal(SurfaceTimelineItemKind.Status, statusItem.Kind);
        Assert.False(statusItem.DisplayInChat);
        Assert.Empty(workspace.GetVisibleTimelineForAgent("owner1:assistant"));
        var agent = Assert.Single(workspace.Agents);
        Assert.True(agent.IsWorking);
        Assert.Equal("Looking up invoice history", agent.StatusText);

        context.Raise(new AgentMessage
        {
            FromHandle = "owner1:assistant",
            ToHandle = "owner1",
            MessageType = "chat",
            Message = "I found the latest invoice."
        });

        var chat = Assert.Single(workspace.GetVisibleTimelineForAgent("owner1:assistant"));
        Assert.Equal(SurfaceTimelineItemKind.Agent, chat.Kind);
        Assert.Equal("I found the latest invoice.", chat.Text);
        Assert.False(agent.IsWorking);
    }

    [Fact]
    public async Task SurfaceWorkspaceRetainsUnderscorePrefixedMessagesWithoutShowingThem()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");

        RaiseChat(context, "owner1:analyst", "_internal note", "hidden-1");

        var hidden = Assert.Single(workspace.GetTimelineForAgent("owner1:analyst"));
        Assert.False(hidden.DisplayInChat);
        Assert.Equal("_internal note", hidden.Text);
        Assert.Empty(workspace.GetVisibleTimelineForAgent("owner1:analyst"));
        Assert.Equal(0, workspace.TotalUnreadCount);
    }

    [Fact]
    public async Task SurfaceWorkspaceTracksUnreadMessagesUntilAgentIsSeen()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:assistant", "AssistantAgent"));
        context.TrackedAgents.Add(new TrackedAgentInfo("owner1:analyst", "AnalystAgent"));
        var workspace = CreateWorkspace(context, new SurfaceOptions { ShowRunningAgentsByDefault = true });

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        workspace.SelectAgent("owner1:assistant");
        context.Raise(new AgentMessage
        {
            FromHandle = "owner1:analyst",
            ToHandle = "owner1",
            MessageType = "chat",
            Message = "Analysis is ready."
        });

        var analyst = workspace.Agents.Single(agent => agent.Handle == "owner1:analyst");
        Assert.True(analyst.HasUnread);
        Assert.Equal(1, analyst.UnreadCount);

        workspace.MarkAgentSeen("owner1:analyst");

        Assert.False(analyst.HasUnread);
        Assert.Equal(0, analyst.UnreadCount);
    }

    [Fact]
    public void SurfaceMessageClassifierClassifiesAdaptiveCardRenderMessages()
    {
        var message = SurfaceMessageFactory.CreateRenderMessage(
            ValidEnvelope(),
            new AgentMessage
            {
                FromHandle = "owner1:assistant",
                ToHandle = "owner1"
            });

        var item = SurfaceMessageClassifier.Classify(message);

        Assert.Equal(SurfaceTimelineItemKind.AdaptiveCard, item.Kind);
        Assert.NotNull(item.Envelope);
        Assert.Equal("valid", item.Envelope!.Id);
    }

    [Fact]
    public void SurfaceMessageClassifierAssociatesAdaptiveCardWithSourceAgent()
    {
        var message = SurfaceMessageFactory.CreateRenderMessage(
            ValidEnvelope(),
            new AgentMessage
            {
                FromHandle = "owner1:crm-agent",
                ToHandle = "owner1:surface"
            },
            "owner1");

        var item = SurfaceMessageClassifier.Classify(message);

        Assert.Equal(SurfaceTimelineItemKind.AdaptiveCard, item.Kind);
        Assert.Equal("owner1:crm-agent", item.AgentHandle);
        Assert.Equal("owner1:surface", item.Author);
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
    public void SurfacePlannerPromptForbidsExecutableActions()
    {
        var service = new SurfaceService(
            "user1:surface",
            new FakeAgentHost("user1:surface"),
            defaultPlanningAgent: null,
            agentFactory: null,
            new SurfaceDefinition { Name = "default" },
            new SurfaceAiOptions(),
            new SurfaceOptions(),
            NullLogger<SurfaceService>.Instance);
        var method = typeof(SurfaceService).GetMethod(
            "BuildSystemPrompt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        var prompt = Assert.IsType<string>(method!.Invoke(service, null));

        Assert.Contains("Planner-generated cards are display-only.", prompt);
        Assert.Contains("Do not include Action.Execute or Action.Submit.", prompt);
        Assert.Contains("Action.ShowCard or Action.ToggleVisibility", prompt);
        Assert.DoesNotContain("Required Adaptive Card actions", prompt);
        Assert.DoesNotContain("Allowed Action.Execute verbs", prompt);
        Assert.DoesNotContain("Allowed target agent overrides", prompt);
    }

    [Fact]
    public void PlannerEnvelopeValidationRejectsExecutableActions()
    {
        var method = typeof(SurfaceService).GetMethod(
            "ValidatePlannerEnvelopeOrThrow",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method!.Invoke(null, [ValidEnvelope()]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Planner-generated cards must be display-only", exception.InnerException!.Message);
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

    private static SurfaceActionContext CreateActionContext()
        => new()
        {
            Envelope = ValidEnvelope(),
            SourceMessage = new AgentMessage
            {
                FromHandle = "user1:assistant",
                ToHandle = "user1",
                TraceId = "trace"
            },
            PrincipalContext = new FakeSurfacePrincipalContext("user1")
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentHealthStatus HealthyAgent(string handle)
        => new()
        {
            Handle = handle,
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true
        };

    private static string WriteSurfaceConfig(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"fabrcore-surface-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private static void RaiseChat(
        FakeSurfacePrincipalContext context,
        string fromHandle,
        string message,
        string id)
        => context.Raise(new AgentMessage
        {
            Id = id,
            FromHandle = fromHandle,
            ToHandle = context.Handle,
            MessageType = "chat",
            Message = message
        });

    private static SurfaceWorkspaceService CreateWorkspace(
        FakeSurfacePrincipalContext context,
        SurfaceOptions? options = null,
        IServiceProvider? serviceProvider = null,
        ISurfaceDiscoveryClient? discoveryClient = null,
        ISurfacePreferencesClient? preferencesClient = null,
        ISurfaceSquadConfigClient? squadConfigClient = null,
        SurfaceBlueprintProvisioner? blueprintProvisioner = null)
        => new(
            Options.Create(options ?? new SurfaceOptions()),
            NullLogger<SurfaceWorkspaceService>.Instance,
            new FakeSurfacePrincipalContextFactory(context),
            serviceProvider,
            discoveryClient,
            preferencesClient: preferencesClient,
            squadConfigClient: squadConfigClient,
            blueprintProvisioner: blueprintProvisioner);

    private static SurfaceBlueprintClient CreateBlueprintClient(RecordingHttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            Options.Create(new SurfaceOptions { FabrCoreHostUrl = "https://fabrcore.test" }),
            NullLogger<SurfaceBlueprintClient>.Instance);

    private static SurfaceSquadConfigClient CreateSquadConfigClient(RecordingHttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            Options.Create(new SurfaceOptions { FabrCoreHostUrl = "https://fabrcore.test" }),
            NullLogger<SurfaceSquadConfigClient>.Instance);

    private static ServiceProvider CreateSurfaceServiceProvider(
        FakeSurfacePrincipalContext context,
        Action<SurfaceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISurfacePrincipalContextFactory>(new FakeSurfacePrincipalContextFactory(context));
        services.AddFabrCoreSurfaceComponents();
        services.Configure<SurfaceOptions>(options => configure?.Invoke(options));
        return services.BuildServiceProvider();
    }

    private sealed class FakeHostApiClient : IFabrCoreHostApiClient
    {
        public string UploadedFileId { get; set; } = "file-id";

        public string? UploadedFileName { get; private set; }

        public int? UploadedTtlSeconds { get; private set; }

        public string? DeletedFileId { get; private set; }

        public Task<string> UploadFileAsync(
            Stream fileStream,
            string fileName,
            int? ttlSeconds = null,
            CancellationToken cancellationToken = default)
        {
            UploadedFileName = fileName;
            UploadedTtlSeconds = ttlSeconds;
            return Task.FromResult(UploadedFileId);
        }

        public Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
        {
            DeletedFileId = fileId;
            return Task.FromResult(true);
        }

        public Task<CreateAgentsResponse> CreateAgentsAsync(
            List<AgentConfiguration> agentConfigurations,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentBlueprintResponse> EnsureBlueprintAgentsAsync(
            string principalHandle,
            AgentBlueprintRequest request,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentHealthStatus> GetAgentHealthAsync(
            string handle,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentMessage> ChatAsync(string handle, string message, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task SendEventAsync(
            string handle,
            EventMessage eventMessage,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ModelConfigResponse> GetModelConfigAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiKeyResponse> GetApiKeyAsync(string alias, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentsListResponse> GetAgentsAsync(string? status = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PrincipalsListResponse> GetPrincipalsAsync(string? status = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentInfo?> GetAgentAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AgentStatisticsResponse> GetAgentStatisticsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PurgeAgentsResponse> PurgeOldAgentsAsync(int olderThanHours = 24, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<Stream?> GetFileAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FileMetadataResponse?> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<EmbeddingResponse> GetEmbeddingsAsync(string text, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<BatchEmbeddingResponse> GetBatchEmbeddingsAsync(
            List<BatchEmbeddingItem> items,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<DiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<AclPrincipal>> GetAclPrincipalsAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclPrincipal?> GetAclPrincipalAsync(
            string callerUserHandle,
            string principalHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpsertAclPrincipalAsync(
            string callerUserHandle,
            AclPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAclPrincipalAsync(
            string callerUserHandle,
            string principalHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<AclRole>> GetAclRolesAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclRole?> GetAclRoleAsync(
            string callerUserHandle,
            string roleName,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpsertAclRoleAsync(
            string callerUserHandle,
            AclRole role,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAclRoleAsync(
            string callerUserHandle,
            string roleName,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<AclGroup>> GetAclGroupsAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclGroup?> GetAclGroupAsync(
            string callerUserHandle,
            string groupName,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpsertAclGroupAsync(
            string callerUserHandle,
            AclGroup group,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAclGroupAsync(
            string callerUserHandle,
            string groupName,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task AddAclGroupMemberAsync(
            string callerUserHandle,
            string groupName,
            GroupMember member,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAclGroupMemberAsync(
            string callerUserHandle,
            string groupName,
            GroupMember member,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<PermissionGrant>> GetAclGrantsAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PermissionGrant?> GetAclGrantAsync(
            string callerUserHandle,
            string grantId,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpsertAclGrantAsync(
            string callerUserHandle,
            PermissionGrant grant,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAclGrantAsync(
            string callerUserHandle,
            string grantId,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<string>> GetPrincipalRolesAsync(
            string callerUserHandle,
            string principalHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<string>> GetPrincipalGroupsAsync(
            string callerUserHandle,
            string principalHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> IsPrincipalInRoleAsync(
            string callerUserHandle,
            string principalHandle,
            string roleName,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclEvaluationResponse> CheckPermissionAsync(
            string callerUserHandle,
            string principalHandle,
            string action,
            string resource = "*:*",
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclEvaluationResponse> EvaluateAclAsync(
            string callerUserHandle,
            AclEvaluationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AclConfigResponse> GetAclConfigAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task SetAclEnforcementModeAsync(
            string callerUserHandle,
            string? mode,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<List<AuditEvent>> GetAuditEventsAsync(
            string callerUserHandle,
            string? category = null,
            string? outcome = null,
            string? subjectPrincipal = null,
            DateTimeOffset? since = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AuditConfigResponse> GetAuditConfigAsync(
            string callerUserHandle,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ChatCompletionResponse> GetChatCompletionAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ChatCompletionResponse> GetChatCompletionAsync(
            string text,
            ChatCompletionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<T?> GetStorageEntityAsync<T>(
            string principal,
            string container,
            string entityKey,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpsertStorageEntityAsync<T>(
            string principal,
            string container,
            string entityKey,
            T entity,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteStorageEntityAsync(
            string principal,
            string container,
            string entityKey,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSurfaceBlueprintClient : ISurfaceBlueprintClient
    {
        public SurfaceBlueprintDocument? StoredBlueprint { get; set; }

        public int GetCount { get; private set; }

        public int SaveCount { get; private set; }

        public int ApplyCount { get; private set; }

        public List<(string PrincipalId, SurfaceBlueprintDocument Request)> AppliedRequests { get; } = [];

        public Task<SurfaceBlueprintDocument?> GetAsync(
            string principalId,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(StoredBlueprint);
        }

        public Task SaveAsync(
            string principalId,
            SurfaceBlueprintDocument blueprint,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            StoredBlueprint = blueprint;
            return Task.CompletedTask;
        }

        public Task<SurfaceBlueprintApplyResult> ApplyAsync(
            string principalId,
            SurfaceBlueprintDocument request,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            AppliedRequests.Add((principalId, request));
            var agents = request.Agents.ToList();
            if (request.Squads.Count > 0)
            {
                var squads = JsonSerializer.SerializeToElement(request.Squads, SurfaceJson.Options);
                var expansion = new SurfaceSquadBlueprintExpander()
                    .ExpandAsync(
                        new FabrCore.Core.Blueprints.BlueprintExpansionContext
                        {
                            PrincipalId = principalId,
                            Blueprint = request
                        },
                        squads,
                        cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                agents.AddRange(expansion.Agents);
            }

            return Task.FromResult(new SurfaceBlueprintApplyResult
            {
                Name = request.Name,
                Version = request.Version,
                TotalRequested = agents.Count,
                SuccessCount = agents.Count,
                Results = agents.Select(config => new AgentHealthStatus
                {
                    Handle = config.Handle ?? principalId,
                    State = HealthState.Healthy,
                    Timestamp = DateTime.UtcNow,
                    IsConfigured = true,
                    Configuration = config
                }).ToList(),
                AgentConfigurationsRequested = agents.Count
            });
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
            => responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                headers,
                body));

            if (responses.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return responses.Dequeue()(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        Dictionary<string, List<string>> Headers,
        string Body);

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

    private sealed class FakeSurfacePrincipalContext : ISurfacePrincipalContext
    {
        public FakeSurfacePrincipalContext(string handle)
        {
            Handle = handle;
        }

        public string Handle { get; }

        public bool IsDisposed { get; private set; }

        public List<AgentMessage> SentMessages { get; } = [];

        public List<AgentMessage> RequestMessages { get; } = [];

        public List<AgentConfiguration> CreatedAgentConfigurations { get; } = [];

        public Dictionary<string, AgentConfiguration> AgentConfigurations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, AgentHealthStatus> AgentHealthStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TrackedAgentInfo> TrackedAgents { get; } = [];

        public List<AgentInfo> SharedAgents { get; } = [];

        public List<bool> GetTrackedAgentsActivateValues { get; } = [];

        public List<(string Handle, HealthDetailLevel DetailLevel)> GetAgentHealthCalls { get; } = [];

        public AgentMessage? ResponseMessage { get; set; }

        public Exception? SendAndReceiveException { get; set; }

        public Exception? GetAgentHealthException { get; set; }

        public event EventHandler<AgentMessage>? AgentMessageReceived;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            RequestMessages.Add(request);
            if (SendAndReceiveException is not null)
            {
                throw SendAndReceiveException;
            }

            return Task.FromResult(ResponseMessage ?? request.Response());
        }

        public Task SendMessage(AgentMessage request)
        {
            SentMessages.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEvent(EventMessage request)
            => Task.CompletedTask;

        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            if (!string.IsNullOrWhiteSpace(agentConfiguration.Handle)
                && !agentConfiguration.Handle.Contains(':', StringComparison.Ordinal))
            {
                agentConfiguration.Handle = $"{Handle}:{agentConfiguration.Handle}";
            }

            CreatedAgentConfigurations.Add(agentConfiguration);
            if (!string.IsNullOrWhiteSpace(agentConfiguration.Handle))
            {
                AgentConfigurations[agentConfiguration.Handle] = agentConfiguration;
                TrackedAgents.RemoveAll(agent => string.Equals(agent.Handle, agentConfiguration.Handle, StringComparison.OrdinalIgnoreCase));
                TrackedAgents.Add(new TrackedAgentInfo(
                    agentConfiguration.Handle,
                    agentConfiguration.AgentType ?? string.Empty)
                {
                    Health = NewHealth(agentConfiguration.Handle, agentConfiguration)
                });
            }

            return Task.FromResult(NewHealth(agentConfiguration.Handle ?? Handle, agentConfiguration));
        }

        public Task<AgentHealthStatus> ResetAgent(string handle)
            => Task.FromResult(NewHealth());

        public Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            GetAgentHealthCalls.Add((handle, detailLevel));
            if (GetAgentHealthException is not null)
            {
                throw GetAgentHealthException;
            }

            if (AgentHealthStatuses.TryGetValue(handle, out var health))
            {
                return Task.FromResult(health);
            }

            var configuration = AgentConfigurations.GetValueOrDefault(handle);
            if (configuration is not null)
            {
                return Task.FromResult(NewHealth(handle, configuration));
            }

            var tracked = TrackedAgents.FirstOrDefault(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase));
            if (tracked is not null)
            {
                return Task.FromResult(tracked.Health ?? NewHealth(handle, new AgentConfiguration
                {
                    Handle = tracked.Handle,
                    AgentType = tracked.AgentType
                }));
            }

            return Task.FromResult(NewUnconfiguredHealth(handle));
        }

        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
        {
            GetTrackedAgentsActivateValues.Add(activate);
            return Task.FromResult(TrackedAgents);
        }

        public Task<bool> IsAgentTracked(string handle)
            => Task.FromResult(TrackedAgents.Any(agent =>
                string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(agent.Handle, $"{Handle}:{handle}", StringComparison.OrdinalIgnoreCase)));

        public Task<List<AgentInfo>> GetAccessibleSharedAgents()
            => Task.FromResult(SharedAgents);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Raise(AgentMessage message)
            => AgentMessageReceived?.Invoke(this, message);

        private AgentHealthStatus NewHealth(string? handle = null, AgentConfiguration? configuration = null)
            => new()
            {
                Handle = handle ?? Handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = configuration
            };

        private AgentHealthStatus NewUnconfiguredHealth(string handle)
            => new()
            {
                Handle = handle,
                State = HealthState.NotConfigured,
                Timestamp = DateTime.UtcNow,
                IsConfigured = false,
                Message = "Agent not configured"
            };
    }

    private sealed class FakeSurfaceDirectMessageSender : ISurfaceDirectMessageSender
    {
        public List<AgentMessage> SentMessages { get; } = [];

        public Task SendMessageAsync(AgentMessage message)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendEventAsync(EventMessage message)
            => Task.CompletedTask;
    }

    private sealed class FakeSurfaceDiscoveryClient : ISurfaceDiscoveryClient
    {
        private readonly SurfaceDiscoveryResponse? response;
        private readonly Exception? exception;

        public FakeSurfaceDiscoveryClient(SurfaceDiscoveryResponse response)
        {
            this.response = response;
        }

        public FakeSurfaceDiscoveryClient(Exception exception)
        {
            this.exception = exception;
        }

        public Task<SurfaceDiscoveryResponse> GetDiscoveryAsync(CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(response ?? new SurfaceDiscoveryResponse());
        }
    }

    private sealed class FakeSurfaceSquadConfigClient : ISurfaceSquadConfigClient
    {
        private readonly Exception? exception;

        public FakeSurfaceSquadConfigClient(IReadOnlyList<SurfaceSquad>? squads = null)
        {
            Squads = squads?.Select(CloneSquad).ToList() ?? [];
        }

        public FakeSurfaceSquadConfigClient(Exception exception)
        {
            this.exception = exception;
            Squads = [];
        }

        public List<SurfaceSquad> Squads { get; private set; }

        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<SurfaceSquad>> GetAsync(
            string principalId,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult<IReadOnlyList<SurfaceSquad>>(Squads.Select(CloneSquad).ToList());
        }

        public Task SaveAsync(
            string principalId,
            IReadOnlyList<SurfaceSquad> squads,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Squads = squads.Select(CloneSquad).ToList();
            return Task.CompletedTask;
        }

        private static SurfaceSquad CloneSquad(SurfaceSquad channel)
            => new()
            {
                SquadType = channel.SquadType,
                Name = channel.Name,
                Slug = channel.Slug,
                PrincipalHandle = channel.PrincipalHandle,
                OrchestratorHandle = channel.OrchestratorHandle,
                Description = channel.Description,
                TaskOptions = new SurfaceTaskSquadOptions
                {
                    WorkerModelName = channel.TaskOptions.WorkerModelName,
                    PersonaPrompt = channel.TaskOptions.PersonaPrompt,
                    ClientAgentOverlay = channel.TaskOptions.ClientAgentOverlay,
                    DelegationTimeoutSeconds = channel.TaskOptions.DelegationTimeoutSeconds,
                    MaxLoopIterations = channel.TaskOptions.MaxLoopIterations
                },
                Agents = channel.Agents.Select(agent => new SurfaceSquadAgent
                {
                    Name = agent.Name,
                    Handle = agent.Handle,
                    AgentType = agent.AgentType,
                    Role = agent.Role,
                    Description = agent.Description
                }).ToList()
            };
    }

    private sealed class FakeSurfacePreferencesClient : ISurfacePreferencesClient
    {
        private readonly Exception? exception;

        public FakeSurfacePreferencesClient(SurfacePreferences? preferences = null)
        {
            Preferences = preferences;
        }

        public FakeSurfacePreferencesClient(Exception exception)
        {
            this.exception = exception;
        }

        public SurfacePreferences? Preferences { get; private set; }

        public int SaveCount { get; private set; }

        public Task<SurfacePreferences> GetAsync(
            string principalId,
            SurfaceOptions defaults,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(Preferences ?? SurfacePreferences.FromDefaults(defaults));
        }

        public Task SaveAsync(
            string principalId,
            SurfacePreferences preferences,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Preferences = new SurfacePreferences
            {
                Version = preferences.Version,
                ShowHiddenAgents = preferences.ShowHiddenAgents,
                ShowRunningAgents = preferences.ShowRunningAgents,
                SurfaceAgentHandles = new HashSet<string>(preferences.SurfaceAgentHandles, StringComparer.OrdinalIgnoreCase)
            };
            return Task.CompletedTask;
        }
    }

    private sealed class JsonResponseHandler : HttpMessageHandler
    {
        private readonly string json;

        public JsonResponseHandler(string json)
        {
            this.json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class FakeSurfacePrincipalContextFactory : ISurfacePrincipalContextFactory
    {
        private readonly FakeSurfacePrincipalContext context;

        public FakeSurfacePrincipalContextFactory(FakeSurfacePrincipalContext context)
        {
            this.context = context;
        }

        public Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
            => Task.FromResult<ISurfacePrincipalContext>(context);

        public Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
            => Task.FromResult<ISurfacePrincipalContext>(context);

        public Task<bool> ReleaseAsync(string handle)
            => Task.FromResult(true);

        public bool HasContext(string handle)
            => true;
    }

    private sealed class FakeAgentHost : IFabrCoreAgentHost
    {
        public FakeAgentHost(string handle)
        {
            Handle = handle;
        }

        public string Handle { get; }

        public List<AgentMessage> SentMessages { get; } = [];

        public List<(string TimerName, string MessageType, string? Message, TimeSpan DueTime, TimeSpan Period)> RegisteredTimers { get; } = [];

        public Dictionary<string, JsonElement> CustomState { get; } = [];

        /// <summary>Requests observed by <see cref="SendAndReceiveMessage"/>, in order.</summary>
        public List<AgentMessage> ReceivedRequests { get; } = [];

        /// <summary>Per-target reply text. Targets without an entry echo an empty response.</summary>
        public Dictionary<string, Func<AgentMessage, string>> Responders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-target artificial latency, used to exercise delegation timeouts.</summary>
        public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string GetHandle() => Handle;

        public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            ReceivedRequests.Add(request);

            if (request.ToHandle is { Length: > 0 } target && Delays.TryGetValue(target, out var delay))
            {
                await Task.Delay(delay);
            }

            var response = request.Response();
            if (request.ToHandle is { Length: > 0 } handle && Responders.TryGetValue(handle, out var responder))
            {
                response.Message = responder(request);
            }

            return response;
        }

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

        public Task SendEvent(EventMessage request)
            => Task.CompletedTask;

        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
            RegisteredTimers.RemoveAll(timer => string.Equals(timer.TimerName, timerName, StringComparison.Ordinal));
            RegisteredTimers.Add((timerName, messageType, message, dueTime, period));
        }

        public void UnregisterTimer(string timerName)
        {
            RegisteredTimers.RemoveAll(timer => string.Equals(timer.TimerName, timerName, StringComparison.Ordinal));
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
            => Task.FromResult(new Dictionary<string, JsonElement>(CustomState));

        public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            foreach (var key in deletes)
            {
                CustomState.Remove(key);
            }

            foreach (var (key, value) in changes)
            {
                CustomState[key] = value;
            }

            return Task.CompletedTask;
        }

        public void SetStatusMessage(string? message)
        {
        }
    }

    private sealed class FakeChatClientService : IFabrCoreChatClientService
    {
        private readonly IChatClient chatClient;

        public FakeChatClientService(IChatClient chatClient)
        {
            this.chatClient = chatClient;
        }

        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
            => Task.FromResult(chatClient);

#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
            => throw new NotSupportedException();
#pragma warning restore MEAI001

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
            => throw new NotSupportedException();

        public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
            => Task.FromResult(new ModelConfiguration
            {
                Name = name,
                Provider = "Test",
                Uri = "http://localhost",
                Model = name,
                ApiKeyAlias = "test"
            });
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string responseText;
        private readonly Queue<ChatResponse> scripted = new();

        private FakeChatClient(string responseText)
        {
            this.responseText = responseText;
        }

        /// <summary>Requests seen by the client, in order. Useful for asserting on composed instructions.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        /// <summary>The <see cref="ChatOptions"/> supplied with each request, in order.</summary>
        public List<ChatOptions?> RequestOptions { get; } = [];

        public static FakeChatClient WithTextResponse(string responseText)
            => new(responseText);

        /// <summary>
        /// Returns each supplied response in turn, then falls back to a terminal text response.
        /// Use with <see cref="ToolCall"/> to drive an agent through a tool-calling sequence.
        /// </summary>
        public static FakeChatClient Scripted(params ChatResponse[] responses)
        {
            var client = new FakeChatClient("Done.");
            foreach (var response in responses)
            {
                client.scripted.Enqueue(response);
            }

            return client;
        }

        public static ChatResponse Text(string text)
            => new(new ChatMessage(ChatRole.Assistant, text));

        public static ChatResponse ToolCall(string callId, string name, string argumentsJson)
        {
            var arguments = JsonSerializer
                .Deserialize<Dictionary<string, JsonElement>>(argumentsJson)!
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. chatMessages]);
            RequestOptions.Add(options);

            return Task.FromResult(scripted.Count > 0
                ? scripted.Dequeue()
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => null;

        public void Dispose()
        {
        }
    }

    [AgentAlias("hidden-test-agent")]
    [FabrCoreHidden]
    private sealed class HiddenTestAgent
    {
    }

    [AgentAlias("surface-test-routing-agent")]
    [Description("Registry description for routing tests")]
    [FabrCoreCapabilities("Handles routing projection tests")]
    [FabrCoreNote("Prefer this test agent for routing projection assertions.")]
    private sealed class SurfaceTestRoutingAgent
    {
    }
}
