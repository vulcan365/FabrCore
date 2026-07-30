using FabrCore.Core;
using FabrCore.SampleApp.Contoso;
using FabrCore.SampleApp.Surface;
using FabrCore.Surface.Ai.Swarm;
using Xunit;

namespace FabrCore.SampleApp.Tests;

public sealed class ContosoBikeShopDemoTests
{
    [Fact]
    public void BlueprintIncludesContosoSwarmSquadWithTenAgents()
    {
        var blueprint = SurfaceDemoBlueprintFactory.Create();

        var contoso = Assert.Single(
            blueprint.Swarm.Squads,
            squad => squad.SquadType == SurfaceSquadType.Swarm);

        Assert.Equal(SurfaceDemoBlueprintFactory.ContosoSquadName, contoso.Name);
        Assert.Equal(10, contoso.Agents.Count);
        Assert.True(contoso.ForceReconfigure);
        Assert.False(string.IsNullOrWhiteSpace(contoso.OrchestratorSystemPrompt));
        Assert.False(string.IsNullOrWhiteSpace(contoso.PlannerSystemPrompt));

        Assert.All(contoso.Agents, agent =>
        {
            Assert.Equal(ContosoWorkerAgent.Alias, agent.AgentType);
            Assert.NotEmpty(agent.Plugins);
            Assert.True(agent.Args.ContainsKey(ContosoWorkerAgent.PersonaArg));
            Assert.True(agent.Args.ContainsKey(ContosoWorkerAgent.FocusArg));
            Assert.True(agent.Args.ContainsKey(ContosoWorkerAgent.PlaybookArg));
        });

        var sme = Assert.Single(contoso.Agents, agent => agent.Role == SurfaceSquadMemberRole.SubjectMatterExpert);
        Assert.Equal("Bike Shop SME", sme.Name);

        var insights = Assert.Single(contoso.Agents, agent => agent.Name == "Customer Insights");
        Assert.Contains(ContosoCrmPlugin.Alias, insights.Plugins);
        Assert.Contains(ContosoHrPlugin.Alias, insights.Plugins);
    }

    [Fact]
    public void ContosoSquadResolvesToExpectedSwarmHandles()
    {
        var blueprint = SurfaceDemoBlueprintFactory.Create();
        var contoso = Assert.Single(
            blueprint.Swarm.Squads,
            squad => squad.SquadType == SurfaceSquadType.Swarm);

        var squad = SurfaceSquadService.BuildSquad(
            SurfaceDemoBlueprintFactory.PrincipalHandle,
            SurfaceSwarmInterop.ToSwarmDefinition(contoso));

        Assert.Equal(SurfaceDemoBlueprintFactory.ContosoSquadOrchestratorHandle, squad.OrchestratorHandle);
        Assert.Equal(10, squad.Agents.Count);
        Assert.All(squad.Agents, agent =>
            Assert.StartsWith($"{SurfaceDemoBlueprintFactory.PrincipalHandle}:squad-contoso-bike-shop-", agent.Handle));
        Assert.Equal(squad.Agents.Count, squad.Agents.Select(agent => agent.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void StoreSeedsCustomersEmployeesAndCampaigns()
    {
        var store = new ContosoBikeShopStore();
        var snapshot = store.GetSnapshot();

        Assert.Equal(12, snapshot.CustomerCount);
        Assert.Equal(8, snapshot.EmployeeCount);
        Assert.Equal(6, snapshot.CampaignCount);
        Assert.True(snapshot.CustomersBySegment.ContainsKey("Club"));
        Assert.True(snapshot.EmployeesByDepartment.ContainsKey("Service"));
        Assert.True(snapshot.CampaignsByStatus.ContainsKey("Running"));
    }

    [Fact]
    public void SeededCustomersOverlapEmployeesByEmailForCrossDomainDemo()
    {
        var store = new ContosoBikeShopStore();
        var employeeEmails = store.SearchEmployees(limit: 100)
            .Select(employee => employee.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overlapping = store.SearchCustomers(limit: 100)
            .Where(customer => employeeEmails.Contains(customer.Email))
            .Select(customer => customer.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.Equal(["Dave Kim", "Priya Sharma", "Rosa Mendez"], overlapping);
    }

    [Fact]
    public void StoreTracksCustomerMutations()
    {
        var store = new ContosoBikeShopStore();

        var added = store.AddCustomer("Wingtip Toys", "orders@wingtiptoys.com", "Wholesale", notes: "Bulk kids-bike order inquiry.");
        Assert.Equal("CUS-9013", added.Id);
        Assert.Equal("Prospect", added.Status);

        var updated = store.UpdateCustomer(added.Id, status: "Active", addPurchaseUsd: 5200m);
        Assert.Equal("Active", updated.Status);
        Assert.Equal(5200m, updated.TotalSpendUsd);

        var fetched = store.GetCustomer(added.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Active", fetched.Status);
        Assert.Equal(13, store.GetSnapshot().CustomerCount);
    }

    [Fact]
    public void StoreTracksEmployeeAndCampaignMutations()
    {
        var store = new ContosoBikeShopStore();

        var hired = store.AddEmployee("Noah Brooks", "noah.brooks@contosobikes.com", "Bike Mechanic", "Service", ptoBalanceDays: 8);
        Assert.Equal("EMP-109", hired.Id);
        Assert.Equal("Active", hired.Status);

        var onLeave = store.UpdateEmployee(hired.Id, status: "On Leave", ptoBalanceDays: 5);
        Assert.Equal("On Leave", onLeave.Status);
        Assert.Equal(5, onLeave.PtoBalanceDays);

        var campaign = store.CreateCampaign("Fall Commuter Push", "Email", "Individual", 900m, "Rain gear bundle offer.");
        Assert.Equal("CAM-307", campaign.Id);
        Assert.Equal("Draft", campaign.Status);

        var running = store.UpdateCampaign(campaign.Id, status: "Running", addLeads: 4);
        Assert.Equal("Running", running.Status);
        Assert.Equal(4, running.LeadsGenerated);
    }

    [Fact]
    public void StoreFiltersBySegmentDepartmentAndStatus()
    {
        var store = new ContosoBikeShopStore();

        Assert.All(store.SearchCustomers(segment: "Wholesale"), customer => Assert.Equal("Wholesale", customer.Segment));
        Assert.All(store.SearchCustomers(status: "Lapsed"), customer => Assert.Equal("Lapsed", customer.Status));
        Assert.All(store.SearchEmployees(department: "Service"), employee => Assert.Equal("Service", employee.Department));
        Assert.All(store.SearchCampaigns(status: "Draft"), campaign => Assert.Equal("Draft", campaign.Status));

        var priya = Assert.Single(store.SearchEmployees(search: "priya.sharma"));
        Assert.Equal("EMP-102", priya.Id);
    }

    [Fact]
    public void WorkerSystemPromptIncludesPersonaFocusAndPlaybook()
    {
        var prompt = ContosoWorkerAgent.BuildSystemPrompt(new AgentConfiguration
        {
            Handle = "demo-user:squad-contoso-bike-shop-customer-insights",
            Args = new Dictionary<string, string>
            {
                [ContosoWorkerAgent.PersonaArg] = "Customer Insights Analyst",
                [ContosoWorkerAgent.FocusArg] = "Cross-reference customers against the employee roster.",
                [ContosoWorkerAgent.PlaybookArg] = "Compare CRM emails against HR emails."
            }
        });

        Assert.Contains("Contoso Bike Shop", prompt);
        Assert.Contains("Customer Insights Analyst", prompt);
        Assert.Contains("Cross-reference customers against the employee roster.", prompt);
        Assert.Contains("Compare CRM emails against HR emails.", prompt);
        Assert.Contains("really tracked in memory", prompt);
    }

    [Fact]
    public void WorkerSystemPromptFallsBackToDescription()
    {
        var prompt = ContosoWorkerAgent.BuildSystemPrompt(new AgentConfiguration
        {
            Handle = "contoso-fallback",
            Description = "Fallback Contoso specialist"
        });

        Assert.Contains("Fallback Contoso specialist", prompt);
        Assert.Contains("Contoso Bike Shop", prompt);
    }
}
