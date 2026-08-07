using System.Text.Json;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ContextCompactionConfigTests
{
    [TestMethod]
    public void IsUsable_RequiresBothWindowAndOutputReserve()
    {
        Assert.IsFalse(new ContextCompactionConfig { MaxContextWindowTokens = 200_000 }.IsUsable,
            "A window with no output reserve leaves the input budget undefined.");
        Assert.IsFalse(new ContextCompactionConfig { MaxOutputTokens = 16_000 }.IsUsable,
            "An output reserve with no window leaves the input budget undefined.");
        Assert.IsTrue(new ContextCompactionConfig { MaxContextWindowTokens = 200_000, MaxOutputTokens = 16_000 }.IsUsable);
    }

    [TestMethod]
    public void IsUsable_RejectsOutOfOrderThresholds()
    {
        var config = new ContextCompactionConfig
        {
            MaxContextWindowTokens = 200_000,
            MaxOutputTokens = 16_000,
            EvictThreshold = 0.8,
            TruncateThreshold = 0.5
        };

        Assert.IsFalse(config.IsUsable, "Truncating before evicting would skip the free rung entirely.");
    }

    [TestMethod]
    public void Rungs_AreComputedFromTheInputBudgetNotTheWholeWindow()
    {
        var config = new ContextCompactionConfig
        {
            MaxContextWindowTokens = 200_000,
            MaxOutputTokens = 16_000
        };

        Assert.AreEqual(184_000, config.InputBudgetTokens);
        Assert.AreEqual(92_000, config.EvictAtTokens);
        Assert.AreEqual(147_200, config.TruncateAtTokens);
    }
}

[TestClass]
public sealed class ContextCompactionStateTests
{
    [TestMethod]
    public void StripSessionState_RemovesTheGroupIndexAndKeepsEverythingElse()
    {
        var payload = Parse($$"""
            {
              "conversationId": "thread-1",
              "stateBag": {
                "TodoProvider": { "todos": [ { "text": "step one" } ] },
                "{{ContextCompaction.StateKey}}": { "messagegroups": [ { "kind": 1 } ] }
              }
            }
            """);

        var stripped = ContextCompaction.StripSessionState(payload);
        var stateBag = stripped.GetProperty("stateBag");

        Assert.IsFalse(stateBag.TryGetProperty(ContextCompaction.StateKey, out _),
            "The context-compaction group index must never reach durable storage.");
        Assert.IsTrue(stateBag.TryGetProperty("TodoProvider", out _),
            "Stripping one provider's state must not disturb the others.");
        Assert.AreEqual("thread-1", stripped.GetProperty("conversationId").GetString());
    }

    [TestMethod]
    public void StripSessionState_IsANoOpWhenTheStateIsAbsent()
    {
        var payload = Parse("""{ "conversationId": "thread-1", "stateBag": { "TodoProvider": {} } }""");

        var stripped = ContextCompaction.StripSessionState(payload);

        Assert.IsTrue(stripped.GetProperty("stateBag").TryGetProperty("TodoProvider", out _));
    }

    [TestMethod]
    public void StripSessionState_LeavesUnexpectedShapesAlone()
    {
        var payload = Parse("""[ 1, 2, 3 ]""");

        var stripped = ContextCompaction.StripSessionState(payload);

        Assert.AreEqual(JsonValueKind.Array, stripped.ValueKind,
            "A snapshot we cannot parse is persisted as-is rather than failing the turn.");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

[TestClass]
public sealed class CompactionLadderTests
{
    [TestMethod]
    public void Describe_RendersEveryRungInOrder()
    {
        var ladder = Build(
            context: new ContextCompactionConfig { MaxContextWindowTokens = 200_000, MaxOutputTokens = 16_000 },
            history: new CompactionConfig { MaxContextTokens = 200_000, Threshold = 0.87 },
            projection: new ProjectionConfig { MaxContextTokens = 200_000, Threshold = 0.9 },
            runSafety: new ChatRunSafetyConfig { MaxPromptInputTokens = 200_000 });

        Assert.AreEqual(
            "evict@92000 → truncate@147200 → history@174000 → fuse@180000 → stop@200000",
            ladder.Describe());
        Assert.IsFalse(ladder.IsOutOfOrder);
    }

    [TestMethod]
    public void Describe_MakesAMissingBoundVisibleRatherThanImplied()
    {
        var ladder = Build(
            context: new ContextCompactionConfig(),
            history: new CompactionConfig { Enabled = false },
            projection: new ProjectionConfig { Enabled = false },
            runSafety: new ChatRunSafetyConfig());

        Assert.AreEqual("context:unconfigured → history:off → fuse:off → stop:off", ladder.Describe());
    }

    [TestMethod]
    public void IsOutOfOrder_FlagsAHistoryRungBelowTheTruncationPoint()
    {
        // The legacy 25000 default against a large window: history compaction would fire long before
        // layer 1 truncates, making the free rung decorative.
        var ladder = Build(
            context: new ContextCompactionConfig { MaxContextWindowTokens = 200_000, MaxOutputTokens = 16_000 },
            history: new CompactionConfig { MaxContextTokens = 25_000, Threshold = 0.75 },
            projection: new ProjectionConfig { MaxContextTokens = 200_000, Threshold = 0.9 },
            runSafety: new ChatRunSafetyConfig { MaxPromptInputTokens = 200_000 });

        Assert.IsTrue(ladder.IsOutOfOrder);
        StringAssert.Contains(ladder.Describe(), "[OUT OF ORDER]");
    }

    private static CompactionLadder Build(
        ContextCompactionConfig context,
        CompactionConfig history,
        ProjectionConfig projection,
        ChatRunSafetyConfig runSafety) =>
        new()
        {
            Context = context,
            History = history,
            Projection = projection,
            RunSafety = runSafety
        };
}
