using FabrCore.Core;
using FabrCore.SampleApp.Surface;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Swarm;
using Xunit;

namespace FabrCore.SampleApp.Tests;

public sealed class SurfaceDemoBlueprintFactoryTests
{
    [Fact]
    public void CreateBuildsAssistantAndFourBranchSquads()
    {
        var blueprint = SurfaceDemoBlueprintFactory.Create();

        Assert.Equal(SurfaceDemoBlueprintFactory.BlueprintName, blueprint.Name);
        Assert.Contains(blueprint.Agents, config =>
            config.Handle == SurfaceDemoBlueprintFactory.SurfaceAgentHandle
            && config.AgentType == "surface");
        Assert.Contains(blueprint.Agents, config =>
            config.Handle == SurfaceDemoBlueprintFactory.CrmAgentHandle
            && config.AgentType == "crm-demo-agent");

        var squads = blueprint.Swarm.Squads;
        Assert.Equal(6, squads.Count);

        var assistant = squads[0];
        Assert.Equal("Assistant", assistant.Name);
        Assert.Equal(SurfaceSquadType.Orchestrator, assistant.SquadType);

        var branchSquads = squads.Skip(1).Where(squad => squad.SquadType == SurfaceSquadType.Orchestrator).ToList();
        Assert.Equal(["Sales", "CRM", "Inventory", "Accounts Receivables"], branchSquads.Select(s => s.Name).ToArray());

        var assistantRuntime = SurfaceBasicSquadService.BuildSquad(SurfaceDemoBlueprintFactory.PrincipalHandle, assistant);
        var expectedBranchHandles = branchSquads
            .Select(squad => SurfaceBasicSquadService.BuildSquad(SurfaceDemoBlueprintFactory.PrincipalHandle, squad).OrchestratorHandle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expectedBranchHandles, assistantRuntime.Agents.Select(agent => agent.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.All(assistant.Agents, agent =>
            Assert.Equal(SurfaceOrchestrationAgentTypes.SquadOrchestrator, agent.AgentType));
    }

    [Fact]
    public void CreateBuildsFourLeafAgentsForEveryBranch()
    {
        var blueprint = SurfaceDemoBlueprintFactory.Create();
        var branches = blueprint.Swarm.Squads.Skip(1)
            .Where(squad => squad.SquadType == SurfaceSquadType.Orchestrator)
            .ToList();

        Assert.All(branches, branch =>
        {
            Assert.Equal(SurfaceSquadType.Orchestrator, branch.SquadType);
            Assert.Equal(4, branch.Agents.Count);
        });

        var crm = Assert.Single(branches, branch => branch.Name == "CRM");
        Assert.Contains(crm.Agents, agent =>
            agent.Handle == SurfaceDemoBlueprintFactory.CrmAgentHandle
            && agent.AgentType == "crm-demo-agent"
            && agent.Name == "CRM Records");
        Assert.Equal(3, crm.Agents.Count(agent => agent.AgentType == SurfaceDemoDomainAgent.Alias));

        foreach (var fakeAgent in branches.SelectMany(branch => branch.Agents)
                     .Where(agent => agent.AgentType == SurfaceDemoDomainAgent.Alias))
        {
            Assert.False(string.IsNullOrWhiteSpace(fakeAgent.Name));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.DomainArg));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.ProfileArg));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.ResponsibilitiesArg));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.RecordsArg));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.DecisionsArg));
            Assert.True(fakeAgent.Args.ContainsKey(SurfaceDemoDomainAgent.HandoffsArg));
            Assert.Contains(SurfaceDemoDomainPlugin.Alias, fakeAgent.Plugins);
        }
    }

    [Fact]
    public void DomainResponseIncludesConfiguredProfileData()
    {
        var response = SurfaceDemoDomainAgent.BuildResponse(
            new AgentConfiguration
            {
                Handle = "lead-intake",
                Description = "Lead intake specialist",
                Args = new Dictionary<string, string>
                {
                    [SurfaceDemoDomainAgent.DomainArg] = "Sales",
                    [SurfaceDemoDomainAgent.ProfileArg] = "Lead Intake",
                    [SurfaceDemoDomainAgent.ResponsibilitiesArg] = "Qualify leads; Route urgent prospects",
                    [SurfaceDemoDomainAgent.RecordsArg] = "LEAD-2048: Contoso expansion inquiry",
                    [SurfaceDemoDomainAgent.DecisionsArg] = "Prioritize funded prospects",
                    [SurfaceDemoDomainAgent.HandoffsArg] = "CRM for account context"
                }
            },
            "Who needs follow-up?");

        Assert.Contains("## Lead Intake", response);
        Assert.Contains("**Domain:** Sales", response);
        Assert.Contains("Who needs follow-up?", response);
        Assert.Contains("Qualify leads", response);
        Assert.Contains("LEAD-2048: Contoso expansion inquiry", response);
        Assert.Contains("Prioritize funded prospects", response);
        Assert.Contains("CRM for account context", response);
    }

    [Fact]
    public void DomainResponseUsesStableFallbacksWhenProfileDataIsMissing()
    {
        var response = SurfaceDemoDomainAgent.BuildResponse(
            new AgentConfiguration
            {
                Handle = "fallback-agent",
                Description = "Fallback Specialist"
            },
            null);

        Assert.Contains("Fallback Specialist", response);
        Assert.Contains("Demo Operations", response);
        Assert.Contains("No user request text supplied.", response);
        Assert.Contains("DEMO-001: Sample record awaiting review", response);
        Assert.Contains("This is deterministic demo output", response);
    }

    [Fact]
    public void DomainSystemPromptRequiresToolGroundedWorkflow()
    {
        var prompt = SurfaceDemoDomainAgent.BuildSystemPrompt(
            new AgentConfiguration
            {
                Handle = "lead-intake",
                Description = "Lead intake specialist",
                Args = new Dictionary<string, string>
                {
                    [SurfaceDemoDomainAgent.DomainArg] = "Sales",
                    [SurfaceDemoDomainAgent.ProfileArg] = "Lead Intake",
                    [SurfaceDemoDomainAgent.ResponsibilitiesArg] = "Qualify leads; Route urgent prospects",
                    [SurfaceDemoDomainAgent.HandoffsArg] = "CRM for account context"
                }
            });

        Assert.Contains("Lead Intake", prompt);
        Assert.Contains("Domain: Sales", prompt);
        Assert.Contains("Call GetDomainBrief", prompt);
        Assert.Contains("UpdateDomainRecord", prompt);
    }

    [Fact]
    public void InMemoryDomainStore_SeedsSearchesAndMutatesRecords()
    {
        var store = new InMemorySurfaceDemoDomainStore();
        store.Seed("demo-user:squad-sales-lead-intake", new SurfaceDemoDomainSeed
        {
            Domain = "Sales",
            Profile = "Lead Intake",
            Responsibilities = ["Qualify leads"],
            Records = ["LEAD-2048: Contoso expansion inquiry"],
            Decisions = ["Prioritize funded prospects"],
            Handoffs = ["CRM for account context"]
        });

        var records = store.SearchRecords("demo-user:squad-sales-lead-intake", "Contoso", 10);
        var seeded = Assert.Single(records);
        Assert.Equal("LEAD-2048", seeded.Id);

        var added = store.AddRecord("demo-user:squad-sales-lead-intake", "New inbound referral", "Open");
        Assert.StartsWith("LEAD-", added.Id);

        var updated = store.UpdateRecord(
            "demo-user:squad-sales-lead-intake",
            added.Id,
            summary: "New inbound referral with budget",
            status: "Pending Review");

        Assert.Equal("Pending Review", updated.Status);
        Assert.Contains("budget", updated.Summary);
    }
}
