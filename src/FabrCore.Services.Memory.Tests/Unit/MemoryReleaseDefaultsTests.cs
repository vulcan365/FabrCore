using FabrCore.Services.Memory.Configuration;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryReleaseDefaultsTests
{
    [TestMethod]
    public void FrozenReleasePolicyDoesNotEnableExperimentalRetrieval()
    {
        var options = new AgentMemoryOptions();
        var expected = new Dictionary<string, object> {
            [nameof(RetrievalOptions.UseCompactSelectionIds)] = false,
            [nameof(RetrievalOptions.PreferMinimalSelection)] = false,
            [nameof(RetrievalOptions.VerifyMultiMemorySelection)] = false,
            [nameof(RetrievalOptions.WarmRetrievalLimit)] = 5,
            [nameof(RetrievalOptions.HeaderScanLimit)] = 200,
            [nameof(RetrievalOptions.UseSemanticCandidates)] = false,
            [nameof(RetrievalOptions.DiversifySemanticCandidates)] = false,
            [nameof(RetrievalOptions.HybridSemanticCandidates)] = false,
            [nameof(RetrievalOptions.SelectionPreviewCharacters)] = 0,
            [nameof(RetrievalOptions.UseMatchedChunkEvidence)] = false,
            [nameof(RetrievalOptions.MatchedChunksPerMemory)] = 1,
            [nameof(RetrievalOptions.SelectMatchedChunks)] = false,
            [nameof(RetrievalOptions.SkipRedundantChunkSelection)] = false,
            [nameof(RetrievalOptions.ChunkSelectionPreviewCharacters)] = 0,
            [nameof(RetrievalOptions.ChunkSelectionIncludeTail)] = false,
            [nameof(RetrievalOptions.SemanticCandidateLimit)] = 20,
            [nameof(RetrievalOptions.RecentCandidateLimit)] = 20,
            [nameof(RetrievalOptions.FreshnessDaysThreshold)] = 1,
            [nameof(RetrievalOptions.RecallGraphHops)] = 1,
            [nameof(RetrievalOptions.PlannerEnabled)] = false,
            [nameof(RetrievalOptions.MaxImaginingQueries)] = 5
        };
        var properties = typeof(RetrievalOptions).GetProperties();
        Assert.HasCount(expected.Count, properties, "Review new retrieval options against docs/memory-release-defaults.md.");
        foreach (var property in properties)
            Assert.AreEqual(expected[property.Name], property.GetValue(options.Retrieval), property.Name);
        Assert.AreEqual(20, options.HotIndex.MaxEntries);
        Assert.AreEqual(3000, options.HotIndex.MaxTokens);
        Assert.IsFalse(options.Consolidation.EnableAutoConsolidation);
        Assert.IsFalse(options.SummaryTree.Enabled);
    }
}
