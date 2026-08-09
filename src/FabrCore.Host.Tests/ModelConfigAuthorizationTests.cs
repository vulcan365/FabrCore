using FabrCore.Core;
using FabrCore.Host.Configuration;
using FabrCore.Host.Security;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class ModelConfigAuthorizationTests
{
    [TestMethod]
    public async Task ModelConfigEndpoints_UseAdminPolicyInsteadOfInteractiveFallback()
    {
        await using var app = await CreateApplicationAsync();
        var client = app.GetTestClient();

        using var interactiveResponse = await client.GetAsync("/interactive-probe");
        Assert.AreEqual(HttpStatusCode.Redirect, interactiveResponse.StatusCode);
        Assert.AreEqual("/interactive-login", interactiveResponse.Headers.Location?.ToString());

        using var modelResponse = await client.GetAsync("/fabrcoreapi/ModelConfig/model/default");
        using var apiKeyResponse = await client.GetAsync("/fabrcoreapi/ModelConfig/apikey/provider");

        Assert.AreEqual(HttpStatusCode.Unauthorized, modelResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, apiKeyResponse.StatusCode);
        Assert.IsNull(modelResponse.Headers.Location);
        Assert.IsNull(apiKeyResponse.Headers.Location);
    }

    [TestMethod]
    public async Task ModelConfigEndpoints_AcceptExistingAdminBearerKey()
    {
        await using var app = await CreateApplicationAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");

        using var modelResponse = await client.GetAsync("/fabrcoreapi/ModelConfig/model/default");
        using var apiKeyResponse = await client.GetAsync("/fabrcoreapi/ModelConfig/apikey/provider");

        Assert.AreEqual(HttpStatusCode.OK, modelResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, apiKeyResponse.StatusCode);
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [FabrCoreConfigurationKeys.AdminApiKey] = "admin-key"
        });
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = InteractiveAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = InteractiveAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, InteractiveAuthenticationHandler>(
                InteractiveAuthenticationHandler.SchemeName,
                _ => { })
            .AddScheme<FabrCoreAdminAuthenticationOptions, FabrCoreAdminAuthenticationHandler>(
                FabrCoreAdminAuthenticationDefaults.Scheme,
                options => options.ApiKey = "admin-key");
        builder.Services.Configure<CloudServerOptions>(_ => { });
        builder.Services.Configure<RemoteAdministrationOptions>(_ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
            options.AddPolicy(
                FabrCoreAdminAuthenticationDefaults.Policy,
                policy => policy
                    .AddAuthenticationSchemes(FabrCoreAdminAuthenticationDefaults.Scheme)
                    .RequireAuthenticatedUser());
        });
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FabrCore.Host.Api.Controllers.ModelConfigController).Assembly);
        builder.Services.AddSingleton<IFabrCoreConfigurationStore>(
            new TestConfigurationStore(CreateConfiguration()));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/interactive-probe", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static FabrCoreConfiguration CreateConfiguration() => new()
    {
        ModelConfigurations =
        [
            new ModelConfiguration
            {
                Name = "default",
                Provider = "OpenAI",
                Uri = "https://openai.test/v1",
                Model = "gpt-test",
                ApiKeyAlias = "provider"
            }
        ],
        ApiKeys = [new ApiKeyConfiguration { Alias = "provider", Value = "provider-key" }]
    };

    private sealed class TestConfigurationStore(FabrCoreConfiguration configuration)
        : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites => false;

        public Task<FabrCoreConfiguration> GetConfigurationAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(configuration);

        public Task SaveConfigurationAsync(
            FabrCoreConfiguration configuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InteractiveAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "InteractiveOidc";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/interactive-login");
            return Task.CompletedTask;
        }
    }
}
