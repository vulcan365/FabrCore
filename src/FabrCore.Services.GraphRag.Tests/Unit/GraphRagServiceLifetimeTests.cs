using FabrCore.Sdk;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class GraphRagServiceLifetimeTests
{
    [TestMethod]
    public void AddGraphRagServices_DoesNotCaptureScopedHostApiClientFromRootProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GraphRagDb"] =
                    "Server=unused;Database=unused;Integrated Security=true;"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        // Matches FabrCore.Surface: the host API client carries request/principal
        // context and is intentionally scoped. The factory must not be invoked
        // while the singleton ingestion service is created from the root provider.
        services.AddScoped<IFabrCoreHostApiClient>(_ =>
            throw new AssertFailedException("Scoped host API client was captured by a root singleton."));

        services.AddGraphRagServices("GraphRagDb");
        services.AddGraphRagAdministration();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        });

        var ingestion = provider.GetRequiredService<IKnowledgeIngestionService>();
        Assert.IsNotNull(ingestion);

        using var scope = provider.CreateScope();
        var adminClient = scope.ServiceProvider.GetRequiredKeyedService<IGraphRagAdminClient>(
            GraphRagAdminClientKeys.Local);
        Assert.IsNotNull(adminClient);
    }
}
