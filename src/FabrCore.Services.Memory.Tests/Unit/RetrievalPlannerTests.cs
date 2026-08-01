using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class RetrievalPlannerTests
{
    [TestMethod]
    public async Task CreatePlan_PlannerDisabledUsesStandardPipeline()
    {
        var plan = await CreatePlanner(new AgentMemoryOptions())
            .CreatePlanAsync("anything", new MemoryIndex());

        CollectionAssert.AreEqual(
            new[] { RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand },
            plan.Steps);
        Assert.AreEqual(RetrievalPlanSource.Disabled, plan.Source);
    }

    [TestMethod]
    public async Task CreatePlan_HeuristicsCoverTrivialTemporalAndProceduralQueries()
    {
        var options = new AgentMemoryOptions();
        options.Retrieval.PlannerEnabled = true;
        var planner = CreatePlanner(options);

        var trivial = await planner.CreatePlanAsync("hello", new MemoryIndex());
        var temporal = await planner.CreatePlanAsync(
            "What did we decide last week about deployment?", new MemoryIndex());
        var procedural = await planner.CreatePlanAsync(
            "Walk me through the customer onboarding workflow", new MemoryIndex());

        CollectionAssert.AreEqual(new[] { RetrievalStep.HotIndexOnly }, trivial.Steps);
        CollectionAssert.Contains(temporal.Steps, RetrievalStep.ArchiveSearch);
        CollectionAssert.Contains(procedural.PreferredTypes!.ToList(), MemoryType.Procedural);
        CollectionAssert.Contains(procedural.PreferredTypes!.ToList(), MemoryType.Instruction);
    }

    [TestMethod]
    public async Task CreatePlan_BroadQueryUsesSummaryTreeWhenEnabled()
    {
        var options = new AgentMemoryOptions();
        options.Retrieval.PlannerEnabled = true;
        options.SummaryTree.Enabled = true;

        var plan = await CreatePlanner(options).CreatePlanAsync(
            "Give me an overview of everything we learned about invoicing", new MemoryIndex());

        Assert.AreEqual(RetrievalStep.SummaryTreeScan, plan.Steps[0]);
    }

    [TestMethod]
    public async Task CreatePlan_StrongHotIndexMatchAvoidsFurtherRetrieval()
    {
        var options = new AgentMemoryOptions();
        options.Retrieval.PlannerEnabled = true;
        var index = new MemoryIndex
        {
            Entries =
            [
                new MemoryIndexEntry
                {
                    Title = "Habitat expense classification",
                    DescriptionHook = "Habitat charges are business meals"
                }
            ]
        };

        var plan = await CreatePlanner(options).CreatePlanAsync(
            "What is the Habitat expense classification?", index);

        CollectionAssert.AreEqual(new[] { RetrievalStep.HotIndexOnly }, plan.Steps);
    }

    [TestMethod]
    public async Task CreatePlan_InconclusiveQueryUsesLlmClassification()
    {
        var options = new AgentMemoryOptions();
        options.Retrieval.PlannerEnabled = true;
        var client = FakeChatClient.WithText(
            """{"tier":"deep","rationale":"Older context is likely needed."}""");
        var services = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client))
            .BuildServiceProvider();

        var plan = await CreatePlanner(options, services).CreatePlanAsync(
            "Compare our deployment decision with the original architecture rationale", new MemoryIndex());

        Assert.AreEqual(RetrievalPlanSource.Llm, plan.Source);
        CollectionAssert.Contains(plan.Steps, RetrievalStep.ArchiveSearch);
        Assert.AreEqual(1, client.CallCount);
    }

    private static RetrievalPlanner CreatePlanner(
        AgentMemoryOptions options, IServiceProvider? services = null) =>
        new(options, services ?? new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
}
