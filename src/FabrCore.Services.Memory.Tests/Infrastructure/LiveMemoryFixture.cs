using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal sealed class LiveMemoryFixture : IAsyncDisposable
{
    private LiveMemoryFixture(
        DatabaseFixture database,
        TestChatClientService chatClientService,
        ServiceProvider services,
        AgentMemoryService memoryService,
        string scope)
    {
        Database = database;
        ChatClientService = chatClientService;
        Services = services;
        Memory = memoryService;
        Scope = scope;
    }

    public DatabaseFixture Database { get; }
    public TestChatClientService ChatClientService { get; }
    public ServiceProvider Services { get; }
    public AgentMemoryService Memory { get; }
    public string Scope { get; }

    public static async Task<LiveMemoryFixture> CreateAsync(string scopePrefix)
    {
        var modelConfiguration = TestEnvironment.RequireLiveModelConfiguration();
        var chatClientService = new TestChatClientService(modelConfiguration);
        var embeddingGenerator = await chatClientService.GetEmbeddingsClient("embeddings");
        var embeddings = new EmbeddingsAdapter(embeddingGenerator);
        var database = await DatabaseFixture.CreateAsync(embeddings);
        var scope = database.CreateScopeKey(scopePrefix);

        database.Options.Retrieval.WarmRetrievalLimit = 2;
        database.Options.Retrieval.HeaderScanLimit = 50;
        database.Options.Retrieval.RecallGraphHops = 1;
        database.Options.Consolidation.EntityMatchThreshold = 0.03;
        database.Options.Consolidation.EnableRelationshipExtraction = false;

        var services = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(chatClientService)
            .BuildServiceProvider();
        var index = new MemoryIndexManager(
            database.Store, database.Options, NullLoggerFactory.Instance);
        var retriever = new MemoryRetriever(
            database.Store, database.Options, services, NullLoggerFactory.Instance);
        var planner = new RetrievalPlanner(
            database.Options, services, NullLoggerFactory.Instance);
        var memory = new AgentMemoryService(
            scope,
            database.Store,
            index,
            retriever,
            Substitute.For<IMemoryCompactor>(),
            planner,
            Substitute.For<IMemorySummaryTree>(),
            database.ScopeService,
            database.AuditLog,
            database.Options,
            services,
            NullLoggerFactory.Instance);

        return new LiveMemoryFixture(database, chatClientService, services, memory, scope);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await Services.DisposeAsync();
    }
}
