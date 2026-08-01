using System.ComponentModel;
using FabrCore.Sdk;

namespace FabrCore.SampleApp.Contoso;

[PluginAlias(Alias)]
[Description("Contoso Bike Shop marketing data plugin with tracked in-memory campaigns.")]
[FabrCoreCapabilities("Lists, searches, creates, and updates Contoso Bike Shop demo marketing campaigns, including status, budget, notes, and lead counts.")]
[FabrCoreNote("Demo-only plugin. Data is fake but every mutation is really applied and shared across all Contoso demo agents.")]
public sealed class ContosoMarketingPlugin : ContosoPluginBase
{
    public const string Alias = "contoso-marketing-data";

    protected override string TableName => "Campaigns";

    [Description("List Contoso Bike Shop marketing campaigns. Optionally filter by search text or status (Draft, Scheduled, Running, Paused, Completed).")]
    public async Task<string> SearchCampaigns(
        [Description("Optional search text matched against id, name, channel, target segment, status, and notes.")] string? search = null,
        [Description("Optional exact status filter: Draft, Scheduled, Running, Paused, or Completed.")] string? status = null,
        [Description("Maximum number of campaigns to return, 1 to 100.")] int limit = 25)
    {
        var results = await RecordDbEffect(
            "SearchCampaigns",
            search ?? "all",
            () => Store.SearchCampaigns(NullIfBlank(search), NullIfBlank(status), limit),
            search, status, limit.ToString());

        return ToJson(new { Count = results.Count, Campaigns = results });
    }

    [Description("Get one Contoso Bike Shop campaign by ID such as CAM-301.")]
    public async Task<string> GetCampaign(
        [Description("Campaign ID such as CAM-301.")] string campaignId)
    {
        var campaign = await RecordDbEffect(
            "GetCampaign",
            campaignId,
            () => Store.GetCampaign(campaignId),
            campaignId);

        return campaign is null
            ? ToJson(new { Error = $"Campaign '{campaignId}' was not found." })
            : ToJson(campaign);
    }

    [Description("Create a new marketing campaign in Draft status. The campaign is really stored in memory and visible to every other agent afterward.")]
    public async Task<string> CreateCampaign(
        [Description("Campaign name.")] string name,
        [Description("Channel: Email, Social, In-Store, or Event.")] string channel = "Email",
        [Description("Target customer segment: Individual, Club, Wholesale, or All.")] string targetSegment = "Individual",
        [Description("Campaign budget in USD.")] decimal budgetUsd = 0,
        [Description("Optional campaign notes, offer details, or copy direction.")] string? notes = null)
    {
        var campaign = await RecordDbEffect(
            "CreateCampaign",
            name,
            () => Store.CreateCampaign(name, channel, targetSegment, budgetUsd, notes),
            name, channel, targetSegment, budgetUsd.ToString());

        return ToJson(new { Message = "Campaign created in Draft status.", Campaign = campaign });
    }

    [Description("Update an existing campaign's status, notes, or budget, or add generated leads. Use status transitions Draft -> Scheduled -> Running -> Completed, or Paused.")]
    public async Task<string> UpdateCampaign(
        [Description("Campaign ID such as CAM-301.")] string campaignId,
        [Description("Optional new status: Draft, Scheduled, Running, Paused, or Completed.")] string? status = null,
        [Description("Optional replacement notes or campaign copy.")] string? notes = null,
        [Description("Optional new budget in USD.")] decimal? budgetUsd = null,
        [Description("Optional number of new leads to add to the campaign's lead count.")] int? addLeads = null)
    {
        var campaign = await RecordDbEffect(
            "UpdateCampaign",
            campaignId,
            () => Store.UpdateCampaign(campaignId, status, notes, budgetUsd, addLeads),
            campaignId, status, notes, budgetUsd?.ToString(), addLeads?.ToString());

        return ToJson(new { Message = "Campaign updated.", Campaign = campaign });
    }

    [Description("Get a summary snapshot of Contoso marketing: campaign counts by status plus customer segment counts for audience planning.")]
    public async Task<string> GetMarketingSnapshot()
    {
        var snapshot = await RecordDbEffect(
            "GetMarketingSnapshot",
            AgentHandle,
            () => Store.GetSnapshot());

        return ToJson(new
        {
            snapshot.CampaignCount,
            snapshot.CampaignsByStatus,
            snapshot.CustomersBySegment
        });
    }
}
