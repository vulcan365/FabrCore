using FabrCore.Sdk;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Infrastructure;

internal sealed class LiveGraphRagFixture : IAsyncDisposable
{
    private LiveGraphRagFixture(
        DatabaseFixture database,
        ServiceProvider serviceProvider,
        IKnowledgeIngestionService ingestion,
        IKnowledgeIngestionService vectorOnlyIngestion,
        IKnowledgeSearchService search,
        string scope)
    {
        Database = database;
        ServiceProvider = serviceProvider;
        Ingestion = ingestion;
        VectorOnlyIngestion = vectorOnlyIngestion;
        Search = search;
        Scope = scope;
    }

    public DatabaseFixture Database { get; }
    public ServiceProvider ServiceProvider { get; }
    public IKnowledgeIngestionService Ingestion { get; }
    public IKnowledgeIngestionService VectorOnlyIngestion { get; }
    public IKnowledgeSearchService Search { get; }
    public string Scope { get; }

    public static async Task<LiveGraphRagFixture> CreateAsync(string prefix)
    {
        var modelConfiguration = TestEnvironment.RequireLiveModelConfiguration();
        var clientService = new LiveChatClientService(modelConfiguration);
        var embeddings = new EmbeddingsAdapter(await clientService.GetEmbeddingsClient("embeddings"));
        var database = await DatabaseFixture.CreateAsync();
        var scope = await database.CreateScopeAsync(prefix);
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(clientService)
            .BuildServiceProvider();

        var ingestion = new KnowledgeIngestionService(
            database.Configuration, NullLogger<KnowledgeIngestionService>.Instance,
            TestEnvironment.ConnectionStringName, database.Audit, embeddings,
            serviceProvider: serviceProvider, extractionModelName: "default");
        var vectorOnlyIngestion = new KnowledgeIngestionService(
            database.Configuration, NullLogger<KnowledgeIngestionService>.Instance,
            TestEnvironment.ConnectionStringName, database.Audit, embeddings);
        var search = new KnowledgeSearchService(
            database.Configuration, NullLogger<KnowledgeSearchService>.Instance,
            TestEnvironment.ConnectionStringName, database.Audit, embeddings);

        return new LiveGraphRagFixture(database, serviceProvider, ingestion, vectorOnlyIngestion, search, scope);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await ServiceProvider.DisposeAsync();
    }
}
