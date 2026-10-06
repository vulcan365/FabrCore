using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk.Tests;

/// <summary>
/// A model that sets a context window but no output limit still gets layer 1: the proxy derives the
/// output reserve, sends no cap to the provider, and keeps the hard stops at the window.
/// </summary>
[TestClass]
public sealed class DerivedOutputReserveTests
{
    [TestMethod]
    [DataRow(8_192, 2_048)]
    [DataRow(32_000, 4_096)]
    [DataRow(128_000, 16_000)]
    [DataRow(200_000, 25_000)]
    [DataRow(262_144, 32_768)]
    [DataRow(1_048_576, 32_768)]
    public void DeriveOutputReserve_ScalesWithWindowAndStaysBelowIt(int window, int expected)
    {
        var reserve = ContextCompaction.DeriveOutputReserve(window);

        Assert.AreEqual(expected, reserve);
        Assert.IsTrue(new ContextCompactionConfig { MaxContextWindowTokens = window, MaxOutputTokens = reserve }.IsUsable);
        Assert.AreEqual(0, ContextCompaction.DeriveOutputReserve(0), "No window means nothing to reserve from.");
    }

    [TestMethod]
    public async Task WindowWithoutOutput_ComposesLayerOneWithDerivedReserve()
    {
        var (agent, _) = await LimitsTestAgent.CreateAsync(window: 128_000);
        var ladder = agent.Ladder!;

        Assert.IsTrue(ladder.Context.IsUsable);
        Assert.IsTrue(ladder.Context.OutputReserveIsDerived);
        Assert.AreEqual(16_000, ladder.Context.MaxOutputTokens);
        Assert.AreEqual(
            "tool-excerpt@56000 → tool-excerpt-tight@89600 → history@78400 → fuse@115200 → stop@128000 (output reserve 16000 derived)",
            ladder.Describe());
        Assert.IsFalse(ladder.IsOutOfOrder);
    }

    [TestMethod]
    public async Task WindowWithoutOutput_HardStopsIgnoreTheDerivedReserve()
    {
        var (agent, client) = await LimitsTestAgent.CreateAsync(window: 128_000);
        var ladder = agent.Ladder!;

        Assert.AreEqual(128_000, ladder.RunSafety.MaxPromptInputTokens, "The stop stays at the window.");
        Assert.AreEqual(128_000, ladder.Projection.MaxContextTokens, "The fuse is anchored to the window.");

        // Between window minus the derived reserve (112,000) and the window: still sent.
        var prompt = new ChatMessage(ChatRole.User, new string('x', 120_000 * 4));
        var tracked = await agent.Client();
        await tracked.GetResponseAsync([prompt]);
        Assert.AreEqual(1, client.CallCount);

        // An operator-stated output limit of the same size does stop it.
        var (stated, statedClient) = await LimitsTestAgent.CreateAsync(window: 128_000, output: 16_000);
        var statedTracked = await stated.Client();
        await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() => statedTracked.GetResponseAsync([prompt]));
        Assert.AreEqual(0, statedClient.CallCount);
    }

    [TestMethod]
    public async Task WindowWithoutOutput_SendsNoProviderOutputCap()
    {
        var (agent, client) = await LimitsTestAgent.CreateAsync(window: 128_000);

        await (await agent.Client()).GetResponseAsync([new(ChatRole.User, "Hi")]);

        Assert.IsNull(client.RequestOptions[0]?.MaxOutputTokens);
    }

    [TestMethod]
    public async Task ExplicitOutput_IsNeverReplacedByTheDerivedReserve()
    {
        var (agent, _) = await LimitsTestAgent.CreateAsync(window: 128_000, output: 8_000);
        var ladder = agent.Ladder!;

        Assert.IsFalse(ladder.Context.OutputReserveIsDerived);
        Assert.AreEqual(8_000, ladder.Context.MaxOutputTokens);
        Assert.AreEqual(120_000, ladder.RunSafety.MaxPromptInputTokens);
        Assert.IsFalse(ladder.Describe().Contains("derived"));
    }

    [TestMethod]
    public async Task AgentArgOutputReserve_CountsAsStated()
    {
        var (agent, _) = await LimitsTestAgent.CreateAsync(
            window: 128_000,
            args: new() { ["_ContextMaxOutputTokens"] = "8000" });
        var ladder = agent.Ladder!;

        Assert.IsFalse(ladder.Context.OutputReserveIsDerived);
        Assert.AreEqual(8_000, ladder.Context.MaxOutputTokens);
        Assert.AreEqual(120_000, ladder.RunSafety.MaxPromptInputTokens);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(16_000)]
    public async Task NoWindow_KeepsTheLegacyLadder(int? output)
    {
        var (agent, _) = await LimitsTestAgent.CreateAsync(window: null, output: output);
        var ladder = agent.Ladder!;

        Assert.IsFalse(ladder.Context.IsUsable, "Without a window there is nothing to derive a reserve from.");
        Assert.IsFalse(ladder.Context.OutputReserveIsDerived);
        Assert.AreEqual("context:unconfigured → history@18750 → fuse@18750 → stop@25000", ladder.Describe());
    }

    [TestMethod]
    public async Task ExplicitHistoryThreshold_WinsOverDerivedMode()
    {
        var (agent, _) = await LimitsTestAgent.CreateAsync(window: 128_000, configureModel: model => model.CompactionThreshold = 0.9);

        Assert.AreEqual(100_800, agent.Ladder!.HistoryAtTokens);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WindowOnlyAgent_ExcerptsOldToolOutputInRun(bool contextCompaction)
    {
        // Ten 2,000-token tool results overflow a 16,000-token window unless layer 1 excerpts them.
        var client = FakeChatClient.Scripted(
        [
            .. Enumerable.Range(0, 10).Select(i => FakeChatClient.ToolCall($"call{i}", "fetch", "{}")),
            FakeChatClient.Text("Finished")
        ]);
        var (agent, _) = await LimitsTestAgent.CreateAsync(
            window: 16_000,
            client: client,
            tools: [AIFunctionFactory.Create(() => new string('x', 8000), "fetch")],
            configureModel: model => model.ContextCompactionEnabled = contextCompaction);

        var response = await agent.AskAsync("Fetch records");

        if (!contextCompaction)
        {
            Assert.AreEqual(SystemMessageTypes.Error, response.MessageType);
            Assert.AreEqual(RunStopReason.PromptTooLarge.ToString(), response.Args!["_fabrcore_run_stop_reason"]);
            return;
        }

        Assert.AreEqual("Finished", response.Message);
        Assert.AreEqual(11, client.CallCount);
        Assert.IsTrue(
            client.Requests[^1].SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Any(result => result.Result is string text && text.Contains("middle omitted")),
            "Older tool results should reach the model as bounded excerpts.");
        Assert.IsTrue(client.RequestOptions.All(options => options?.MaxOutputTokens is null));
    }
}
