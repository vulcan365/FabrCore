using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Tests.Evaluation;

[TestClass]
[TestCategory("Evaluation")]
public sealed class MemoryQualityEvaluationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task ExtractionEval_CapturesDurableKnowledgeWithoutTransientLeakage()
    {
        await using var fixture = await LiveMemoryFixture.CreateAsync("eval-extraction");
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, """
                Please remember two durable practices for future work:
                1. Every public API response must use camelCase JSON properties.
                2. Our release workflow is: validate database migrations, deploy blue/green,
                   then run the smoke-test suite before shifting all traffic.

                For temporary context only, the incident dashboard has 7 alerts right now and
                I am on call until 5 PM today. Those current values should not become long-term memory.
                """),
            new(ChatRole.Assistant, "Understood. I will follow those durable API and release practices.")
        };

        var extracted = await fixture.Memory.ExtractMemoriesAsync(conversation);
        var combined = string.Join("\n", extracted.Select(m =>
            $"{m.Type}: {m.Title} {m.Description} {m.Content}"));
        var durableHits = 0;
        if (combined.Contains("camelCase", StringComparison.OrdinalIgnoreCase)) durableHits++;
        if (combined.Contains("blue/green", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("smoke", StringComparison.OrdinalIgnoreCase)) durableHits++;
        var transientLeaks = new[] { "7 alerts", "on call until 5" }
            .Count(term => combined.Contains(term, StringComparison.OrdinalIgnoreCase));

        TestContext.WriteLine($"Extracted {extracted.Count} memories");
        TestContext.WriteLine($"Durable recall: {durableHits}/2; transient leakage: {transientLeaks}/2");
        TestContext.WriteLine(combined);

        Assert.AreEqual(2, durableHits,
            "The extraction model must retain both explicitly durable practices.");
        Assert.AreEqual(0, transientLeaks,
            "Current dashboard/on-call values must not leak into long-term memory.");
        Assert.IsTrue(extracted.Any(m => m.Type == MemoryType.Rule));
        Assert.IsTrue(extracted.Any(m => m.Type == MemoryType.Procedural));
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task RetrievalEval_LlmSelectionAndVectorArchiveFindExpectedMemories()
    {
        await using var fixture = await LiveMemoryFixture.CreateAsync("eval-retrieval");
        var memories = new Dictionary<string, MemoryEntry>
        {
            ["format"] = await fixture.Memory.SaveMemoryAsync(
                "Response format preference", MemoryType.Instruction,
                "Use concise Markdown tables for comparisons and do not use emojis.",
                "Concise tables without emojis"),
            ["refund"] = await fixture.Memory.SaveMemoryAsync(
                "Refund verification policy", MemoryType.Rule,
                "Always verify the current order status before offering a customer a refund.",
                "Check order status before refunds"),
            ["staging"] = await fixture.Memory.SaveMemoryAsync(
                "Staging database topology", MemoryType.Fact,
                "The staging environment shares its SQL database with the QA environment.",
                "Staging and QA share SQL"),
            ["onboarding"] = await fixture.Memory.SaveMemoryAsync(
                "Customer onboarding workflow", MemoryType.Procedural,
                "Validate required fields, create the customer record, then send the welcome email.",
                "Validate, create, and welcome new customers"),
            ["inventory"] = await fixture.Memory.SaveMemoryAsync(
                "Plate inventory snapshot", MemoryType.Observation,
                "There were 847 plates in inventory when the report was generated.",
                "Historical plate count", isPointInTime: true)
        };

        var scenarios = new[]
        {
            new Scenario("How should I format a comparison for this user?", memories["format"].Id),
            new Scenario("What must I check before I offer a customer a refund?", memories["refund"].Id),
            new Scenario("Walk me through onboarding a new customer.", memories["onboarding"].Id),
            new Scenario("Does the staging environment use the QA SQL database?", memories["staging"].Id)
        };

        var llmReciprocalRanks = new List<double>();
        var vectorReciprocalRanks = new List<double>();
        foreach (var scenario in scenarios)
        {
            var recall = await fixture.Memory.RecallAsync(scenario.Query);
            var recallIds = recall.WarmMemories.Select(m => m.Id).ToList();
            var llmRank = RankOf(recallIds, scenario.ExpectedMemoryId);
            llmReciprocalRanks.Add(llmRank == 0 ? 0 : 1d / llmRank);

            var archive = await fixture.Memory.SearchArchiveAsync(scenario.Query, limit: 3);
            var archiveIds = archive.Select(r => r.Entry.Id).ToList();
            var vectorRank = RankOf(archiveIds, scenario.ExpectedMemoryId);
            vectorReciprocalRanks.Add(vectorRank == 0 ? 0 : 1d / vectorRank);

            TestContext.WriteLine(
                $"{scenario.Query}\n  LLM rank={llmRank}: {string.Join(" | ", recall.WarmMemories.Select(m => m.Title))}" +
                $"\n  Vector rank={vectorRank}: {string.Join(" | ", archive.Select(r => $"{r.Entry.Title} ({r.Distance:F3})"))}");
        }

        var llmRecallAt2 = llmReciprocalRanks.Count(x => x > 0) / (double)scenarios.Length;
        var llmMrr = llmReciprocalRanks.Average();
        var vectorRecallAt3 = vectorReciprocalRanks.Count(x => x > 0) / (double)scenarios.Length;
        var vectorMrr = vectorReciprocalRanks.Average();

        TestContext.WriteLine(
            $"LLM Recall@2={llmRecallAt2:P0}, MRR={llmMrr:F3}; " +
            $"Vector Recall@3={vectorRecallAt3:P0}, MRR={vectorMrr:F3}");

        Assert.AreEqual(1d, llmRecallAt2, 0.001,
            "Every scenario's expected memory should be selected within the two-memory warm budget.");
        Assert.IsGreaterThanOrEqualTo(0.75, llmMrr,
            "Relevant memories should usually be the first LLM-selected result.");
        Assert.AreEqual(1d, vectorRecallAt3, 0.001,
            "Every scenario's expected memory should appear in the top three semantic results.");
        Assert.IsGreaterThanOrEqualTo(0.65, vectorMrr,
            "Semantic retrieval should rank expected memories near the top.");
    }

    private static int RankOf(IReadOnlyList<Guid> ids, Guid expected)
    {
        var index = ids.ToList().IndexOf(expected);
        return index < 0 ? 0 : index + 1;
    }

    private sealed record Scenario(string Query, Guid ExpectedMemoryId);
}
