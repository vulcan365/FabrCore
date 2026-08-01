using System.ComponentModel;
using FabrCore.Sdk;

namespace FabrCore.SampleApp.Contoso;

[PluginAlias(Alias)]
[Description("Contoso Bike Shop CRM data plugin with tracked in-memory customers.")]
[FabrCoreCapabilities("Lists, searches, creates, and updates Contoso Bike Shop demo customers in a shared in-memory CRM that persists for the lifetime of the app.")]
[FabrCoreNote("Demo-only plugin. Data is fake but every mutation is really applied and shared across all Contoso demo agents.")]
public sealed class ContosoCrmPlugin : ContosoPluginBase
{
    public const string Alias = "contoso-crm-data";

    protected override string TableName => "Customers";

    [Description("List Contoso Bike Shop customers. Optionally filter by search text, segment (Individual, Club, Wholesale), or status (Active, Prospect, Lapsed).")]
    public async Task<string> SearchCustomers(
        [Description("Optional search text matched against id, name, email, segment, status, and notes.")] string? search = null,
        [Description("Optional exact segment filter: Individual, Club, or Wholesale.")] string? segment = null,
        [Description("Optional exact status filter: Active, Prospect, or Lapsed.")] string? status = null,
        [Description("Maximum number of customers to return, 1 to 100.")] int limit = 25)
    {
        var results = await RecordDbEffect(
            "SearchCustomers",
            search ?? "all",
            () => Store.SearchCustomers(NullIfBlank(search), NullIfBlank(segment), NullIfBlank(status), limit),
            search, segment, status, limit.ToString());

        return ToJson(new { Count = results.Count, Customers = results });
    }

    [Description("Get one Contoso Bike Shop customer by ID such as CUS-9001.")]
    public async Task<string> GetCustomer(
        [Description("Customer ID such as CUS-9001.")] string customerId)
    {
        var customer = await RecordDbEffect(
            "GetCustomer",
            customerId,
            () => Store.GetCustomer(customerId),
            customerId);

        return customer is null
            ? ToJson(new { Error = $"Customer '{customerId}' was not found." })
            : ToJson(customer);
    }

    [Description("Add a new customer to the Contoso Bike Shop CRM. The customer is really stored in memory and visible to every other agent afterward.")]
    public async Task<string> AddCustomer(
        [Description("Customer or organization name.")] string name,
        [Description("Customer email address.")] string email,
        [Description("Segment: Individual, Club, or Wholesale.")] string segment = "Individual",
        [Description("Status: Active, Prospect, or Lapsed.")] string? status = null,
        [Description("Optional short notes about the customer.")] string? notes = null)
    {
        var customer = await RecordDbEffect(
            "AddCustomer",
            email,
            () => Store.AddCustomer(name, email, segment, status, notes),
            name, email, segment, status);

        return ToJson(new { Message = "Customer added to the Contoso CRM.", Customer = customer });
    }

    [Description("Update an existing Contoso customer's status, segment, or notes, or record an additional purchase amount.")]
    public async Task<string> UpdateCustomer(
        [Description("Customer ID such as CUS-9001.")] string customerId,
        [Description("Optional new status: Active, Prospect, or Lapsed.")] string? status = null,
        [Description("Optional new segment: Individual, Club, or Wholesale.")] string? segment = null,
        [Description("Optional replacement notes.")] string? notes = null,
        [Description("Optional purchase amount in USD to add to the customer's total spend.")] decimal? addPurchaseUsd = null)
    {
        var customer = await RecordDbEffect(
            "UpdateCustomer",
            customerId,
            () => Store.UpdateCustomer(customerId, status, segment, notes, addPurchaseUsd),
            customerId, status, segment, notes, addPurchaseUsd?.ToString());

        return ToJson(new { Message = "Customer updated.", Customer = customer });
    }

    [Description("Get a summary snapshot of the Contoso CRM: customer counts by segment and status. Useful for reports and planning.")]
    public async Task<string> GetCrmSnapshot()
    {
        var snapshot = await RecordDbEffect(
            "GetCrmSnapshot",
            AgentHandle,
            () => Store.GetSnapshot());

        return ToJson(new
        {
            snapshot.CustomerCount,
            snapshot.CustomersBySegment,
            snapshot.CustomersByStatus
        });
    }
}
