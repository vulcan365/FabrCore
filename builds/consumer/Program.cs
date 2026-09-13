using System.Reflection;
using FabrCore.Core;
using FabrCore.Host;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.TestHost;

foreach (var name in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "packages.txt")))
    Assembly.Load(name);

var connection = Environment.GetEnvironmentVariable("FABRCORE_SMOKE_CONNECTION_STRING");
foreach (var sql in string.IsNullOrWhiteSpace(connection) ? new[] { false } : new[] { false, true })
{
    for (var run = 0; run < (sql ? 2 : 1); run++)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["FabrCore:AdminAuthentication:ApiKey"] = "package-smoke-admin";
        builder.Configuration["FabrCore:Host:AllowedWebSocketOrigins:0"] = "http://localhost";
        if (sql) builder.Configuration["ConnectionStrings:FabrCore"] = connection;
        builder.Configuration["FabrCore:Database:AutoInitialize"] = (run == 0).ToString();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies = [typeof(MyAssistantAgent).Assembly] }
            .UseConfigurationStore<SmokeConfiguration>());
        await using var app = builder.Build();
        app.UseFabrCoreServer();
        await app.StartAsync();
        try
        {
            if (app.Services.GetRequiredService<FabrCoreFeatureState>().DatabaseEnabled != sql)
                throw new InvalidOperationException("Unexpected database mode.");
            var registry = app.Services.GetRequiredService<IFabrCoreRegistry>();
            if (registry.FindAgentType("my-assistant") is null) throw new InvalidOperationException("README agent was not discovered.");
            using var client = app.GetTestClient();
            (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "package-smoke-admin");
            var administration = new FabrCoreAdministrationClient(client);
            var capabilities = await administration.GetCapabilitiesAsync();
            if (!capabilities.ToString().Contains("admin-conversations")) throw new InvalidOperationException("Administration capabilities are missing.");
            if (run == 0) await administration.SaveBlueprintAsync("smoke", "consumer-blueprint", new() { Name = "consumer-blueprint" }, "*");
            var blueprintPage = await administration.GetBlueprintPageAsync("smoke");
            if (!blueprintPage.Items.Any(b => b.Name == "consumer-blueprint")) throw new InvalidOperationException("Typed blueprint summaries did not round trip.");
            var diagnostic = await administration.CreateAdminSessionAsync("smoke", "diagnostic-target", new());
            await administration.DeleteAdminSessionAsync("smoke", "diagnostic-target", diagnostic.Id);
            var storage = app.Services.GetRequiredService<IUserScopedFabrCoreStorageProvider>();
            if (run == 0) await storage.UpsertAsync("smoke", "package-consumer", "key", "persisted");
            if (await storage.GetAsync<string>("smoke", "package-consumer", "key") != "persisted")
                throw new InvalidOperationException("Package storage did not survive restart.");
        }
        finally { await app.StopAsync(); }
    }
}
Console.WriteLine("Package consumer startup, readiness, discovery, and storage checks passed.");

public sealed class SmokeConfiguration : IFabrCoreConfigurationStore
{
    public bool SupportsWrites => false;
    public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FabrCoreConfiguration
    {
        ModelConfigurations =
        [
            new() { Name = "default", Provider = "OpenAI", Uri = "https://example.invalid", ApiKeyAlias = "", Model = "test" },
            new() { Name = "embeddings", Provider = "OpenAI", Uri = "https://example.invalid", ApiKeyAlias = "", Model = "test-embeddings" }
        ]
    });
    public Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
