using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.SampleApp.Surface;
using FabrCore.Sdk;
using FabrCore.Surface.Builders;
using FabrCore.Surface.Contracts;

namespace FabrCore.SampleApp.Crm;

public sealed class CrmDemoTools(InMemoryCrmStore store, IFabrCoreAgentHost agentHost)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Description("Render CRM UI as deterministic agent-owned Adaptive Cards. Use this for every visual view, list, card, grouped overview, and form. Pass the CRM data and desired layout in natural language.")]
    public async Task<string> RenderCrmSurfaceView(
        [Description("Natural language render request. Include all known CRM data needed for the view.")] string renderRequest)
    {
        renderRequest = EnsureCustomerViewData(renderRequest);
        var renderId = $"crm-view-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var envelope = BuildEnvelope(renderId, renderRequest);

        await agentHost.SendMessage(new AgentMessage
        {
            ToHandle = TargetHandle,
            FromHandle = agentHost.GetHandle(),
            Message = "CRM Surface view",
            MessageType = SurfaceMessageTypes.UiRender,
            DataType = SurfaceMessageTypes.DataType,
            Data = JsonSerializer.SerializeToUtf8Bytes(envelope, global::FabrCore.Surface.SurfaceJson.Options),
            Kind = MessageKind.OneWay,
            Args = new Dictionary<string, string>
            {
                [SurfaceMessageArgs.SurfaceSourceHandle] = agentHost.GetHandle()
            }
        });

        return "Rendered CRM Surface view.";
    }

    [Description("Search demo CRM customers and return customer records as JSON data for reasoning or Surface render prompts.")]
    public string SearchCustomers(
        [Description("Optional search text for customer id, name, status, segment, owner, or notes.")] string? search = null,
        [Description("Optional exact status filter such as Active, Prospect, At Risk, Paused, or Closed.")] string? status = null)
        => ToJson(store.SearchCustomers(NullIfBlank(search), NullIfBlank(status))
            .Select(WithCustomerActions));

    [Description("Search demo CRM contacts and return contact records as JSON data for reasoning or Surface render prompts.")]
    public string SearchContacts(
        [Description("Optional search text for contact name, title, or email.")] string? search = null,
        [Description("Optional customer ID such as CUS-1001.")] string? customerId = null)
        => ToJson(store.SearchContacts(NullIfBlank(search), NullIfBlank(customerId)));

    [Description("Get one customer plus that customer's contacts as JSON data for reasoning or Surface render prompts.")]
    public string GetCustomerWithContacts(
        [Description("Customer ID such as CUS-1001.")] string customerId)
    {
        var customer = store.GetCustomer(customerId);
        if (customer is null)
            return ToJson(new { error = $"Customer '{customerId}' was not found." });

        return ToJson(new
        {
            customer = WithCustomerActions(customer),
            contacts = store.SearchContacts(customerId: customer.Id)
        });
    }

    [Description("Get all customers with contacts nested under each customer. Use this when the user asks for grouped customer/contact views.")]
    public string GetCustomersGroupedWithContacts(
        [Description("Optional search text for customer id, name, status, segment, owner, or notes.")] string? search = null,
        [Description("Optional exact status filter such as Active, Prospect, At Risk, Paused, or Closed.")] string? status = null)
        => ToJson(store.SearchCustomers(NullIfBlank(search), NullIfBlank(status))
            .Select(customer => new
            {
                customer = WithCustomerActions(customer),
                contacts = store.SearchContacts(customerId: customer.Id)
            }));

    [Description("Create a customer directly in the demo CRM and return the created customer plus contacts as JSON data. Render any UI by calling RenderCrmSurfaceView afterward.")]
    public string CreateCustomer(
        [Description("Customer name.")] string name,
        [Description("Customer segment or industry.")] string segment,
        [Description("Account owner.")] string owner,
        [Description("Status such as Prospect, Active, At Risk, Paused, or Closed.")] string status = "Prospect",
        [Description("Annual revenue estimate.")] decimal annualRevenue = 0,
        [Description("Short account notes.")] string? notes = null)
    {
        var customer = store.CreateCustomer(name, segment, owner, status, annualRevenue, notes);
        return GetCustomerWithContacts(customer.Id);
    }

    [Description("Create a contact directly in the demo CRM and return the parent customer plus contacts as JSON data. Render any UI by calling RenderCrmSurfaceView afterward.")]
    public string CreateContact(
        [Description("Customer ID such as CUS-1001.")] string customerId,
        [Description("Contact full name.")] string fullName,
        [Description("Contact title.")] string title,
        [Description("Contact email.")] string email,
        [Description("Contact phone.")] string phone = "",
        [Description("Whether this is the primary contact.")] bool primary = false)
    {
        var contact = store.CreateContact(customerId, fullName, title, email, phone, primary);
        return GetCustomerWithContacts(contact.CustomerId);
    }

    [Description("Update a customer's status directly and return the customer plus contacts as JSON data. Render any UI by calling RenderCrmSurfaceView afterward.")]
    public string UpdateCustomerStatus(
        [Description("Customer ID such as CUS-1001.")] string customerId,
        [Description("New status such as Prospect, Active, At Risk, Paused, or Closed.")] string status)
    {
        var customer = store.UpdateCustomerStatus(customerId, status);
        return GetCustomerWithContacts(customer.Id);
    }

    private string TargetHandle
    {
        get
        {
            var owner = agentHost.GetUserHandle();
            return string.IsNullOrWhiteSpace(owner) ? "demo-user" : owner;
        }
    }

    private static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private string EnsureCustomerViewData(string renderRequest)
    {
        using var embeddedData = TryExtractJson(renderRequest);
        if (embeddedData is not null)
            return renderRequest;

        string? data = null;
        if (FindToken(renderRequest, "CUS-") is { } customerId
            && store.GetCustomer(customerId) is not null)
        {
            data = GetCustomerWithContacts(customerId);
        }
        else if (LooksLikeCustomerListRequest(renderRequest))
        {
            data = GetCustomersGroupedWithContacts(status: FindRequestedStatus(renderRequest));
        }

        if (data is null)
            return renderRequest;

        return $"""
            {renderRequest}

            CRM data:
            {data}
            """;
    }

    private static bool LooksLikeCustomerListRequest(string renderRequest)
    {
        var lower = renderRequest.ToLowerInvariant();
        return lower.Contains("customer", StringComparison.Ordinal)
            && (lower.Contains("list", StringComparison.Ordinal)
                || lower.Contains("all customers", StringComparison.Ordinal)
                || lower.Contains("browse", StringComparison.Ordinal)
                || lower.Contains("overview", StringComparison.Ordinal));
    }

    private static string? FindRequestedStatus(string renderRequest)
        => new[] { "Active", "Prospect", "At Risk", "Paused", "Closed" }
            .FirstOrDefault(status => renderRequest.Contains(status, StringComparison.OrdinalIgnoreCase));

    private object BuildEnvelope(string renderId, string renderRequest)
    {
        var card = new Dictionary<string, object?>
        {
            ["type"] = "AdaptiveCard",
            ["version"] = "1.6",
            ["body"] = BuildCardBody(renderRequest)
        };

        if (BuildCardActions(renderRequest) is { Count: > 0 } actions)
            card["actions"] = actions;

        return new Dictionary<string, object?>
        {
            ["version"] = "2.0",
            ["id"] = renderId,
            ["card"] = card,
            ["data"] = new Dictionary<string, object?>
            {
                ["source"] = "crm-demo",
                ["request"] = renderRequest
            },
            ["metadata"] = new Dictionary<string, object?>
            {
                ["targetHandle"] = TargetHandle,
                ["source"] = agentHost.GetHandle()
            }
        };
    }

    private static List<object> BuildCardBody(string renderRequest)
    {
        var lower = renderRequest.ToLowerInvariant();
        if (lower.Contains("create customer form", StringComparison.Ordinal))
            return BuildCreateCustomerFormBody();

        if (lower.Contains("add contact form", StringComparison.Ordinal)
            || lower.Contains("create contact", StringComparison.Ordinal))
            return BuildContactFormBody(renderRequest);

        if (lower.Contains("update status form", StringComparison.Ordinal))
            return BuildStatusFormBody(renderRequest);

        var body = new List<object>
        {
            TextBlock("CRM view", weight: "Bolder", size: "Medium")
        };

        using var data = TryExtractJson(renderRequest);
        if (data is null)
        {
            body.Add(TextBlock(SummarizeRequest(renderRequest), wrap: true));
            return body;
        }

        var root = data.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryGet(root, "error", out var error))
        {
            body.Add(TextBlock(error.ToString(), wrap: true));
            return body;
        }

        if (root.ValueKind == JsonValueKind.Object && TryGet(root, "customer", out var customer))
        {
            body.Add(CustomerContainer(customer, TryGet(root, "contacts", out var contacts) ? contacts : default));
            return body;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in root.EnumerateArray())
            {
                count++;
                if (TryGet(item, "customer", out var groupedCustomer))
                    body.Add(CustomerContainer(groupedCustomer, TryGet(item, "contacts", out var contacts) ? contacts : default));
                else
                    body.Add(CustomerContainer(item, default));
            }

            if (count == 0)
                body.Add(TextBlock("No CRM records matched.", wrap: true));

            return body;
        }

        body.Add(TextBlock(root.ToString(), wrap: true));
        return body;
    }

    private static List<object>? BuildCardActions(string renderRequest)
    {
        var lower = renderRequest.ToLowerInvariant();
        if (lower.Contains("create customer form", StringComparison.Ordinal))
        {
            return
            [
                SurfaceActions.ToBoth(
                    title: "Create customer",
                    verb: CrmSurfaceIds.CreateCustomer,
                    targetAgent: "crm-agent",
                    messageTemplate: "create customer")
            ];
        }

        if (lower.Contains("add contact form", StringComparison.Ordinal)
            || lower.Contains("create contact", StringComparison.Ordinal))
        {
            var customerId = FindToken(renderRequest, "CUS-") ?? "";
            return
            [
                SurfaceActions.ToBoth(
                    title: "Create contact",
                    verb: CrmSurfaceIds.CreateContact,
                    targetAgent: "crm-agent",
                    payload: new { customerId },
                    messageTemplate: "create contact for customer {customerId}")
            ];
        }

        if (lower.Contains("update status form", StringComparison.Ordinal))
        {
            var customerId = FindToken(renderRequest, "CUS-") ?? "";
            return
            [
                SurfaceActions.ToBoth(
                    title: "Update status",
                    verb: CrmSurfaceIds.UpdateCustomerStatus,
                    targetAgent: "crm-agent",
                    payload: new { customerId },
                    messageTemplate: "update status for customer {customerId}")
            ];
        }

        return null;
    }

    private static List<object> BuildCreateCustomerFormBody()
        =>
        [
            TextBlock("Create customer", weight: "Bolder", size: "Medium"),
            InputText("name", "Customer name"),
            InputText("segment", "Segment"),
            InputText("owner", "Owner"),
            ChoiceSet("status", "Status", "Prospect", "Prospect", "Active", "At Risk", "Paused", "Closed"),
            InputNumber("annualRevenue", "Annual revenue"),
            InputText("notes", "Notes", isMultiline: true)
        ];

    private static List<object> BuildContactFormBody(string renderRequest)
        =>
        [
            TextBlock("Create contact", weight: "Bolder", size: "Medium"),
            InputText("customerId", "Customer ID", FindToken(renderRequest, "CUS-")),
            InputText("fullName", "Full name"),
            InputText("title", "Title"),
            InputText("email", "Email"),
            InputText("phone", "Phone"),
            new Dictionary<string, object?>
            {
                ["type"] = "Input.Toggle",
                ["id"] = "primary",
                ["title"] = "Primary contact",
                ["value"] = "false"
            }
        ];

    private static List<object> BuildStatusFormBody(string renderRequest)
        =>
        [
            TextBlock("Update status", weight: "Bolder", size: "Medium"),
            InputText("customerId", "Customer ID", FindToken(renderRequest, "CUS-")),
            ChoiceSet("status", "Status", "Active", "Prospect", "Active", "At Risk", "Paused", "Closed"),
            InputText("reason", "Reason", isMultiline: true)
        ];

    private static object CustomerContainer(JsonElement customer, JsonElement contacts)
    {
        var customerId = Text(customer, "customerId", "id") ?? "";
        var name = Text(customer, "name") ?? customerId;
        var items = new List<object>
        {
            TextBlock(name, weight: "Bolder", wrap: true),
            new Dictionary<string, object?>
            {
                ["type"] = "FactSet",
                ["facts"] = new object[]
                {
                    Fact("ID", customerId),
                    Fact("Status", Text(customer, "status") ?? ""),
                    Fact("Segment", Text(customer, "segment") ?? ""),
                    Fact("Owner", Text(customer, "owner") ?? ""),
                    Fact("Revenue", Text(customer, "annualRevenue") ?? "")
                }
            }
        };

        if (Text(customer, "notes") is { Length: > 0 } notes)
            items.Add(TextBlock(notes, wrap: true, spacing: "Small"));

        if (contacts.ValueKind == JsonValueKind.Array)
        {
            var contactLines = contacts.EnumerateArray()
                .Select(contact => "- " + string.Join(" | ", new[]
                {
                    Text(contact, "fullName"),
                    Text(contact, "title"),
                    Text(contact, "email")
                }.Where(v => !string.IsNullOrWhiteSpace(v))))
                .Where(line => line.Length > 2)
                .ToList();

            if (contactLines.Count > 0)
            {
                items.Add(TextBlock("Contacts", weight: "Bolder", spacing: "Medium"));
                items.Add(TextBlock(string.Join("\n", contactLines), wrap: true, spacing: "Small"));
            }
        }

        items.Add(new Dictionary<string, object?>
        {
            ["type"] = "ActionSet",
            ["actions"] = new[]
            {
                SurfaceActions.ToAgent(
                    title: "View",
                    verb: CrmSurfaceIds.CustomerView,
                    targetAgent: "crm-agent",
                    payload: new { customerId },
                    messageTemplate: "show me the customer view for customer {customerId}")
            }
        });

        return new Dictionary<string, object?>
        {
            ["type"] = "Container",
            ["separator"] = true,
            ["spacing"] = "Medium",
            ["items"] = items
        };
    }

    private static object TextBlock(
        string? text,
        string? weight = null,
        string? size = null,
        bool wrap = true,
        string? spacing = null)
    {
        var block = new Dictionary<string, object?>
        {
            ["type"] = "TextBlock",
            ["text"] = string.IsNullOrWhiteSpace(text) ? "-" : text,
            ["wrap"] = wrap
        };
        if (!string.IsNullOrWhiteSpace(weight))
            block["weight"] = weight;
        if (!string.IsNullOrWhiteSpace(size))
            block["size"] = size;
        if (!string.IsNullOrWhiteSpace(spacing))
            block["spacing"] = spacing;
        return block;
    }

    private static object InputText(string id, string label, string? value = null, bool isMultiline = false)
        => new Dictionary<string, object?>
        {
            ["type"] = "Input.Text",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value,
            ["isMultiline"] = isMultiline
        };

    private static object InputNumber(string id, string label)
        => new Dictionary<string, object?>
        {
            ["type"] = "Input.Number",
            ["id"] = id,
            ["label"] = label
        };

    private static object ChoiceSet(string id, string label, string value, params string[] choices)
        => new Dictionary<string, object?>
        {
            ["type"] = "Input.ChoiceSet",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value,
            ["choices"] = choices.Select(choice => new Dictionary<string, object?>
            {
                ["title"] = choice,
                ["value"] = choice
            }).ToArray()
        };

    private static object Fact(string title, string value)
        => new Dictionary<string, object?>
        {
            ["title"] = title,
            ["value"] = string.IsNullOrWhiteSpace(value) ? "-" : value
        };

    private static JsonDocument? TryExtractJson(string text)
    {
        var marker = text.IndexOf("CRM data:", StringComparison.OrdinalIgnoreCase);
        var candidate = marker >= 0 ? text[(marker + "CRM data:".Length)..] : text;
        var start = candidate.IndexOfAny(['{', '[']);
        if (start < 0)
            return null;

        candidate = candidate[start..].Trim();
        try
        {
            return JsonDocument.Parse(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? Text(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGet(element, propertyName, out var value))
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        return null;
    }

    private static string? FindToken(string text, string prefix)
    {
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var end = start + prefix.Length;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
            end++;

        return end == start + prefix.Length
            ? null
            : text[start..end].ToUpperInvariant();
    }

    private static string SummarizeRequest(string text)
        => text.Length <= 600 ? text : text[..600] + "...";

    private static object WithCustomerActions(Customer customer) => new
    {
        customer.Id,
        CustomerId = customer.Id,
        customer.Name,
        customer.Segment,
        customer.Status,
        customer.Owner,
        customer.AnnualRevenue,
        customer.Notes,
        customer.UpdatedUtc,
        WorkflowActions = CreateCustomerActions(customer)
    };

    private static object CreateCustomerActions(Customer customer) => new
    {
        ViewCustomer = SurfaceActions.ToAgent(
            title: "View",
            verb: CrmSurfaceIds.CustomerView,
            targetAgent: "crm-agent",
            payload: new { customerId = customer.Id },
            messageTemplate: "show me the customer view for customer {customerId}")
    };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
