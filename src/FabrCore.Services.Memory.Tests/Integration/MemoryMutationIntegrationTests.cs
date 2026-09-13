using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.Memory.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class MemoryMutationIntegrationTests
{
    [TestMethod]
    public async Task CorrectionUpdatesContentAndInvalidatesDerivedSummary()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        var scope = database.CreateScopeKey("correction");
        using var services = new ServiceCollection().BuildServiceProvider();
        var memory = Create(database, scope, services);
        var entry = await memory.SaveMemoryAsync("Port", MemoryType.Fact, "Use port 8123.");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var insert = new SqlCommand("INSERT INTO mem.MemorySummaryNode (ScopeKey,Topic,Summary) VALUES (@scope,'Port','Use port 8123.')", connection))
        {
            insert.Parameters.AddWithValue("@scope", scope);
            await insert.ExecuteNonQueryAsync();
        }
        await memory.UpdateMemoryAsync(entry.Id, content: "Use port 9443.", description: "Current port");
        Assert.AreEqual("Use port 9443.", (await database.Store.GetPrimaryChunkAsync(scope, entry.Id))!.Content);
        Assert.AreEqual("Current port", (await database.Store.GetEntityByIdAsync(scope, entry.Id))!.Description);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM mem.MemorySummaryNode WHERE ScopeKey=@scope", connection);
        count.Parameters.AddWithValue("@scope", scope);
        Assert.AreEqual(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task ExtractionReceiptSurvivesNewFacadeAndDoesNotResurrectForgottenMemory()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        var scope = database.CreateScopeKey("extraction-retry");
        var client = FakeChatClient.WithText("""{"memories":[{"title":"Port","type":"Fact","content":"Use port 8123."}]}""");
        using var services = new ServiceCollection().AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client)).BuildServiceProvider();
        var memory = Create(database, scope, services);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Remember that the service uses port 8123.") };
        var first = await memory.ExtractMemoriesAsync(messages);
        var fresh = Create(database, scope, services);
        var replay = await fresh.ExtractMemoriesAsync(messages);
        Assert.AreEqual(first.Single().Id, replay.Single().Id);
        Assert.AreEqual(1, client.CallCount);
        await fresh.ForgetMemoryAsync(first[0].Id);
        Assert.IsEmpty(await fresh.ExtractMemoriesAsync(messages));
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task FailedExtractionRollsBackEarlierMemoriesAndCanRetry()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        var scope = database.CreateScopeKey("extraction-rollback");
        database.Options.AllowedMemoryTypes = [MemoryType.Fact];
        var client = FakeChatClient.WithSequentialResponses(
            """{"memories":[{"title":"Port","type":"Fact","content":"Use port 8123."},{"title":"Policy","type":"Rule","content":"Verify first."}]}""",
            """{"memories":[{"title":"Port","type":"Fact","content":"Use port 8123."}]}""");
        using var services = new ServiceCollection().AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client)).BuildServiceProvider();
        var memory = Create(database, scope, services);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Remember the port and verification policy.") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.ExtractMemoriesAsync(messages));
        Assert.IsEmpty(await database.Store.GetHeadersAsync(scope, 100));
        Assert.IsNull(await database.Store.GetIndexContentAsync(scope));
        var retried = await memory.ExtractMemoriesAsync(messages);
        Assert.HasCount(1, retried);
        Assert.AreEqual(2, client.CallCount);
    }

    private static AgentMemoryService Create(DatabaseFixture database, string scope, IServiceProvider services)
    {
        var store = new SqlMemoryStore(database.Options, database.Configuration, NullLoggerFactory.Instance);
        return new(scope, store, new MemoryIndexManager(store, database.Options, NullLoggerFactory.Instance),
            Substitute.For<IMemoryRetriever>(), Substitute.For<IMemoryCompactor>(), Substitute.For<IRetrievalPlanner>(),
            Substitute.For<IMemorySummaryTree>(), database.ScopeService, database.AuditLog, database.Options, services, NullLoggerFactory.Instance);
    }
}
