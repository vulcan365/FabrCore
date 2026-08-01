using System.Text.Json;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Tests.Evaluation;

[TestClass]
[TestCategory("Evaluation")]
public sealed class GraphRagQualityEvaluationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveVectorRetrieval_EnforcesRecallAtThreeAndMeanReciprocalRank()
    {
        await using var fixture = await LiveGraphRagFixture.CreateAsync("eval-retrieval");
        var documents = new Dictionary<string, string>
        {
            ["apollo.md"] = "Project Apollo deploys its SQL database with a blue-green cutover and validates migrations first.",
            ["orion.md"] = "Project Orion rotates signing certificates monthly and stores secrets in a managed vault.",
            ["zephyr.md"] = "The Zephyr incident runbook begins by draining the message queue before restarting workers.",
            ["onboarding.md"] = "Customer onboarding validates required fields, creates the account, then sends a welcome email."
        };
        foreach (var (fileName, content) in documents)
            await fixture.VectorOnlyIngestion.IngestDocumentAsync(new KnowledgeIngestionRequest(fileName, fixture.Scope, content));

        var scenarios = new[]
        {
            new Scenario("How do we safely deploy the SQL database migration?", "apollo.md"),
            new Scenario("Where are signing secrets stored and when are certificates rotated?", "orion.md"),
            new Scenario("What is the first action in the worker incident runbook?", "zephyr.md"),
            new Scenario("What sequence do we use for a new customer account?", "onboarding.md")
        };

        var reciprocalRanks = new List<double>();
        foreach (var scenario in scenarios)
        {
            var json = await fixture.Search.SearchChunksAsync(new ScopedSearchRequest(scenario.Query, [fixture.Scope], 3));
            using var result = JsonDocument.Parse(json);
            var names = result.RootElement.EnumerateArray()
                .Select(r => r.GetProperty("entityName").GetString()!)
                .ToArray();
            var index = Array.FindIndex(names, n => n.Equals(scenario.ExpectedDocument, StringComparison.OrdinalIgnoreCase));
            var rank = index < 0 ? 0 : index + 1;
            reciprocalRanks.Add(rank == 0 ? 0 : 1d / rank);
            TestContext.WriteLine($"{scenario.Query}\n  rank={rank}: {string.Join(" | ", names)}");
        }

        var recallAtThree = reciprocalRanks.Count(r => r > 0) / (double)scenarios.Length;
        var meanReciprocalRank = reciprocalRanks.Average();
        TestContext.WriteLine($"Recall@3={recallAtThree:P0}; MRR={meanReciprocalRank:F3}");

        Assert.AreEqual(1d, recallAtThree, 0.001, "Every gold document must be present in the first three chunks.");
        Assert.IsGreaterThanOrEqualTo(0.70, meanReciprocalRank, "Gold documents should normally rank first.");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveExtraction_CreatesExpectedEntitiesRelationshipAndSearchableEvidence()
    {
        await using var fixture = await LiveGraphRagFixture.CreateAsync("eval-extraction");
        const string content = """
            # Apollo ownership and deployment

            Project Apollo is owned by Dana Ruiz. Apollo runs on SQL Server 2025.
            Dana Ruiz approves every Apollo database migration before the platform team
            performs a blue-green deployment. Treat Apollo, Dana Ruiz, and SQL Server 2025
            as durable entities and capture their relationships.
            """;

        var ingest = await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "apollo-ownership.md", fixture.Scope, content,
            "Extract named projects, people, technologies, and explicit ownership or approval relationships."));

        await using var connection = new SqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT Name FROM grag.KnowledgeEntity WHERE ScopeKey = @scope AND EntityType <> 'Document';
            SELECT COUNT(*) FROM grag.KnowledgeRelationship WHERE ScopeKey = @scope;
            """, connection);
        command.Parameters.AddWithValue("@scope", fixture.Scope);
        var entityNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) entityNames.Add(reader.GetString(0));
        Assert.IsTrue(await reader.NextResultAsync());
        Assert.IsTrue(await reader.ReadAsync());
        var relationshipCount = reader.GetInt32(0);

        var combinedNames = string.Join(" | ", entityNames);
        var expectedEntityHits = new[] { "Apollo", "Dana", "SQL Server" }
            .Count(term => combinedNames.Contains(term, StringComparison.OrdinalIgnoreCase));
        var retrieval = await fixture.Search.SearchChunksAsync(new ScopedSearchRequest(
            "Who approves Apollo database migrations?", [fixture.Scope], 3));

        TestContext.WriteLine($"Extracted entities ({ingest.ExtractedEntityCount}): {combinedNames}");
        TestContext.WriteLine($"Relationships: {relationshipCount}");
        TestContext.WriteLine($"Retrieval: {retrieval}");

        Assert.IsGreaterThanOrEqualTo(2, expectedEntityHits, "At least two of the three explicit durable entities must be extracted.");
        Assert.IsGreaterThanOrEqualTo(1, relationshipCount, "The explicit ownership/approval relation must be represented in the graph.");
        StringAssert.Contains(retrieval, "Dana Ruiz");
    }

    private sealed record Scenario(string Query, string ExpectedDocument);
}
