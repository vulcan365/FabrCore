using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.SampleApp.Surface;
using FabrCore.Sdk;
using FabrCore.Surface.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.SampleApp.Crm;

[AgentAlias("crm-demo-agent")]
[Description("Demo CRM assistant that manages customers and contacts and renders trusted Surface UI.")]
[FabrCoreCapabilities("Searches, creates, and updates demo CRM customers and contacts while rendering Surface UI intents.")]
[FabrCoreNote("Uses in-memory seeded CRM data; data resets when SurfaceApp restarts.")]
public sealed class CrmDemoAgent(
    AgentConfiguration config,
    IServiceProvider serviceProvider,
    IFabrCoreAgentHost fabrcoreAgentHost)
    : FabrCoreAgentProxy(config, serviceProvider, fabrcoreAgentHost)
{
    private const string ProactiveTestTimerMessageType = "timer:crm-demo-proactive-test";
    private const string ProactiveTestMessageType = "crm.proactive-test";
    private const string Microsoft365CopilotChannel = "m365copilot";
    private const string Microsoft365CopilotDeliveryEndpointIdArg = "Microsoft365Copilot:DeliveryEndpointId";
    private static readonly TimeSpan ProactiveTestDelay = TimeSpan.FromSeconds(30);

    private const string DefaultPrompt = """
        You are the SurfaceApp CRM demo agent. You help users create and manage customers and contacts in a small demo CRM.

        Use the CRM tools whenever the user asks to browse, search, create, update, or inspect CRM records.
        The CRM tools render deterministic Adaptive Card Surface UI for CRM views and forms.
        Do not ask the built-in surface agent to invent executable actions.

        Workflow:
        - Use CRM data tools to fetch or mutate customer/contact data.
        - Then call RenderCrmSurfaceView with a clear natural-language request and the relevant CRM data.
        - RenderCrmSurfaceView owns customer View actions and form submit actions.
        - For grouped customer/contact views, call GetCustomersGroupedWithContacts and ask RenderCrmSurfaceView to keep contacts nested under their customer.
        - For create or update forms, ask RenderCrmSurfaceView to render the relevant form; the tool owns the action wiring.
        - When the user asks to add or create a contact for a customer but has not supplied all contact fields, render an add-contact form instead of asking for the details in chat.
        - Add-contact forms must include customerId, fullName, title, email, phone, and primary fields, with customerId prefilled when known.

        Keep chat responses brief and explain what UI you rendered. Never invent real external CRM access; this is seeded demo data.
        """;

    private static readonly Regex CustomerIdRegex = new(@"\bCUS-\d{4,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private AIAgent? _agent;
    private AgentSession? _session;
    private CrmDemoTools? _crmTools;
    private InMemoryCrmStore? _store;
    private ILogger<CrmDemoAgent> Logger => serviceProvider.GetRequiredService<ILogger<CrmDemoAgent>>();

    public override async Task OnInitialize()
    {
        _store = serviceProvider.GetRequiredService<InMemoryCrmStore>();
        _crmTools = new CrmDemoTools(_store, fabrcoreAgentHost);

        var tools = await ResolveConfiguredToolsAsync();
        tools.AddRange(typeof(CrmDemoTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false).Length > 0)
            .Select(m => AIFunctionFactory.Create(m, _crmTools))
            .Cast<AITool>());

        config.SystemPrompt = string.IsNullOrWhiteSpace(config.SystemPrompt)
            ? DefaultPrompt
            : $"{config.SystemPrompt}\n\n{DefaultPrompt}";

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);

        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        if (message.MessageType == ProactiveTestTimerMessageType)
        {
            var timerState = ParseProactiveTestTimerState(message.Message);
            if (!string.IsNullOrWhiteSpace(timerState.TimerName))
                fabrcoreAgentHost.UnregisterTimer(timerState.TimerName);

            var target = string.IsNullOrWhiteSpace(timerState.DeliveryEndpointId)
                ? null
                : new PrincipalDeliveryTarget(Microsoft365CopilotChannel, timerState.DeliveryEndpointId);

            Logger.LogInformation(
                "Sending CRM proactive test message - Channel: {Channel}, EndpointCaptured: {EndpointCaptured}",
                target?.Channel ?? "principal-default",
                target is not null);

            await SendToUserAsync(
                "Proactive messaging test: this message was sent 30 seconds after your CRM request.",
                messageType: ProactiveTestMessageType,
                target: target);
            return message.Response();
        }

        var response = await HandleMessageAsync(message);
        ScheduleProactiveTestMessage(message);
        return response;
    }

    private async Task<AgentMessage> HandleMessageAsync(AgentMessage message)
    {
        if (message.MessageType == SurfaceMessageTypes.UiRender)
        {
            var renderResponse = message.Response();
            renderResponse.Message = "Surface render received.";
            return await PublishOneWayResponseAsync(message, renderResponse);
        }

        if (message.MessageType == SurfaceMessageTypes.UiAction)
            return await PublishOneWayResponseAsync(message, await HandleSurfaceAction(message));

        if (await TryHandleDirectFormRequest(message) is { } formResponse)
            return await PublishOneWayResponseAsync(message, formResponse);

        var response = message.Response();
        if (_agent is null || _session is null)
        {
            response.Message = "CRM demo agent is not initialized.";
            return await PublishOneWayResponseAsync(message, response);
        }

        SetStatusMessage("Working in the demo CRM...");
        await foreach (var update in _agent.RunStreamingAsync(new ChatMessage(ChatRole.User, message.Message), _session))
        {
            response.Message += update.Text;
        }

        SetStatusMessage(null);
        return await PublishOneWayResponseAsync(message, response);
    }

    private void ScheduleProactiveTestMessage(AgentMessage sourceMessage)
    {
        var timerName = $"crm-demo-proactive-test-{Guid.NewGuid():N}";
        string? deliveryEndpointId = null;
        sourceMessage.Args?.TryGetValue(Microsoft365CopilotDeliveryEndpointIdArg, out deliveryEndpointId);
        var isMicrosoft365Copilot = string.Equals(
            sourceMessage.Channel,
            Microsoft365CopilotChannel,
            StringComparison.OrdinalIgnoreCase);

        if (isMicrosoft365Copilot && string.IsNullOrWhiteSpace(deliveryEndpointId))
        {
            Logger.LogWarning(
                "Microsoft 365 Copilot did not capture a proactive delivery endpoint for this turn; " +
                "the delayed CRM test message will remain pending for the principal.");
        }

        Logger.LogInformation(
            "Scheduling CRM proactive test message in {DelaySeconds} seconds - Channel: {Channel}, EndpointCaptured: {EndpointCaptured}",
            ProactiveTestDelay.TotalSeconds,
            sourceMessage.Channel ?? "unspecified",
            !string.IsNullOrWhiteSpace(deliveryEndpointId));

        var timerState = JsonSerializer.Serialize(new ProactiveTestTimerState(timerName, deliveryEndpointId));
        fabrcoreAgentHost.RegisterTimer(
            timerName: timerName,
            messageType: ProactiveTestTimerMessageType,
            message: timerState,
            dueTime: ProactiveTestDelay,
            // Orleans treats TimeSpan.Zero as an immediate repeat, not a one-shot period.
            period: Timeout.InfiniteTimeSpan);
    }

    private static ProactiveTestTimerState ParseProactiveTestTimerState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new ProactiveTestTimerState(string.Empty, null);

        try
        {
            return JsonSerializer.Deserialize<ProactiveTestTimerState>(value)
                ?? new ProactiveTestTimerState(value, null);
        }
        catch (JsonException)
        {
            // Backward compatibility for timers registered by the earlier test implementation.
            return new ProactiveTestTimerState(value, null);
        }
    }

    private sealed record ProactiveTestTimerState(string TimerName, string? DeliveryEndpointId);

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;

    private async Task<AgentMessage?> TryHandleDirectFormRequest(AgentMessage message)
    {
        if (_crmTools is null || string.IsNullOrWhiteSpace(message.Message))
            return null;

        if (!LooksLikeAddContactFormRequest(message.Message))
            return null;

        var response = message.Response();
        var customerId = FindCustomerId(message.Message);
        if (customerId is null)
        {
            response.Message = "Which customer should I add the contact to?";
            return response;
        }

        var resolvedCustomerId = ResolveCustomerId(customerId);
        if (resolvedCustomerId is null)
        {
            response.Message = $"I could not find customer {customerId}.";
            return response;
        }

        await RenderAddContactForm(resolvedCustomerId);
        response.Message = $"Rendered the add contact form for customer {resolvedCustomerId}.";
        return response;
    }

    private async Task<AgentMessage> HandleSurfaceAction(AgentMessage message)
    {
        var response = message.Response();
        var action = TryReadAction(message);
        if (action is null)
        {
            response.Message = "I received a Surface action, but could not read its payload.";
            return response;
        }

        var selectedId = ExtractRecordId(action);

        if (MatchesAction(action, CrmSurfaceIds.CreateCustomer) && _crmTools is not null)
        {
            if (action.Result?.Success == true)
            {
                var customerId = ResolveCustomerId(selectedId);
                var data = customerId is null
                    ? _crmTools.SearchCustomers()
                    : _crmTools.GetCustomerWithContacts(customerId);
                await _crmTools.RenderCrmSurfaceView($"""
                    Render the newly created customer. If customer-level data is present, show a customer record with contacts nested underneath.

                    CRM data:
                    {data}
                    """);
                response.Message = action.Result.Message ?? "Created customer.";
                return response;
            }

            await _crmTools.RenderCrmSurfaceView("""
                Render a create customer form with fields for name, segment, owner, status, annual revenue, and notes.
                """);
            response.Message = "Rendered the create customer form.";
            return response;
        }

        if (MatchesAction(action, CrmSurfaceIds.CreateContact) && _crmTools is not null)
        {
            var customerId = ResolveCustomerId(selectedId);
            if (action.Result?.Success == true)
            {
                customerId = ResolveCustomerId(ExtractRecordId(action));
                if (customerId is null)
                {
                    response.Message = action.Result.Message ?? "Created contact.";
                    return response;
                }

                var data = _crmTools.GetCustomerWithContacts(customerId);
                await _crmTools.RenderCrmSurfaceView($"""
                    Render the customer record and contacts after a contact was created. Keep contacts nested under the customer.

                    CRM data:
                    {data}
                    """);
                response.Message = action.Result.Message ?? $"Created contact for customer {customerId}.";
                return response;
            }

            if (TryCreateContactFromAction(action) is { } created)
            {
                var data = _crmTools.GetCustomerWithContacts(created.CustomerId);
                await _crmTools.RenderCrmSurfaceView($"""
                    Render the customer record and contacts after a contact was created. Keep contacts nested under the customer.

                    CRM data:
                    {data}
                    """);
                response.Message = $"Created contact {created.FullName}.";
                return response;
            }

            if (customerId is null)
            {
                response.Message = "I could not find the customer for the contact form.";
                return response;
            }

            var customerData = _crmTools.GetCustomerWithContacts(customerId);
            await RenderAddContactForm(customerId, customerData);
            response.Message = action.Result?.Message ?? $"Rendered the add contact form for customer {customerId}.";
            return response;
        }

        if (MatchesAction(action, CrmSurfaceIds.UpdateCustomerStatus) && _crmTools is not null)
        {
            var customerId = ResolveCustomerId(selectedId);
            if (customerId is null)
            {
                response.Message = "I could not find the customer for the status update form.";
                return response;
            }

            if (action.Result?.Success == true)
            {
                var data = _crmTools.GetCustomerWithContacts(customerId);
                await _crmTools.RenderCrmSurfaceView($"""
                    Render the customer record and contacts after the status update. Keep contacts nested under the customer.

                    CRM data:
                    {data}
                    """);
                response.Message = action.Result.Message ?? $"Updated customer {customerId}.";
                return response;
            }

            var customerData = _crmTools.GetCustomerWithContacts(customerId);
            await _crmTools.RenderCrmSurfaceView($"""
                Render an update status form for customer {customerId}.
                Include customer id, status select options, and reason fields.

            CRM data:
            {customerData}
            """);
            response.Message = $"Rendered the update status form for customer {customerId}.";
            return response;
        }

        if (IsCustomerViewAction(action) && selectedId is not null && _crmTools is not null)
        {
            var customerId = ResolveCustomerId(selectedId);
            if (customerId is null)
            {
                response.Message = $"I could not find a customer for {selectedId}.";
                return response;
            }

            var data = _crmTools.GetCustomerWithContacts(customerId);
            await _crmTools.RenderCrmSurfaceView($"""
                Render a customer record followed immediately by that customer's contacts.
                Keep contacts nested under the customer record.

            CRM data:
            {data}
            """);
            response.Message = $"Rendered the customer view for customer {customerId}.";
            return response;
        }

        response.Message = action.Result?.Message ?? $"Received Surface action {action.ActionId ?? action.Verb}.";
        return response;
    }

    private static AdaptiveCardActionEvent? TryReadAction(AgentMessage message)
    {
        if (message.Data is null || message.Data.Length == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize<AdaptiveCardActionEvent>(message.Data, global::FabrCore.Surface.SurfaceJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AgentMessage> PublishOneWayResponseAsync(AgentMessage request, AgentMessage response)
    {
        if (request.Kind != MessageKind.OneWay || string.IsNullOrWhiteSpace(response.Message))
            return response;

        var targetHandle = !string.IsNullOrWhiteSpace(request.FromHandle)
            ? request.FromHandle
            : fabrcoreAgentHost.GetUserHandle();
        if (string.IsNullOrWhiteSpace(targetHandle))
            return response;

        await fabrcoreAgentHost.SendMessage(new AgentMessage
        {
            FromHandle = config.Handle ?? fabrcoreAgentHost.GetHandle(),
            ToHandle = targetHandle,
            Message = response.Message,
            MessageType = response.MessageType,
            Kind = MessageKind.OneWay
        });

        return response;
    }

    private string? ResolveCustomerId(string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
            return null;

        if (selectedId.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase))
            return selectedId;

        if (selectedId.StartsWith("CON-", StringComparison.OrdinalIgnoreCase))
            return _store?.GetContact(selectedId)?.CustomerId;

        return selectedId;
    }

    private async Task RenderAddContactForm(string customerId, string? customerData = null)
    {
        if (_crmTools is null)
            return;

        customerData ??= _crmTools.GetCustomerWithContacts(customerId);
        await _crmTools.RenderCrmSurfaceView($"""
            Render an add contact form for customer {customerId}.
            This is a blank data-entry form, not a request for more chat input.
            Include Adaptive Card inputs with these exact ids: customerId, fullName, title, email, phone, primary.
            Prefill customerId with "{customerId}" and make it visible or clearly labeled.
            Use Input.Text for customerId, fullName, title, email, and phone.
            Use Input.Toggle for primary.

            CRM data:
            {customerData}
            """);
    }

    private Contact? TryCreateContactFromAction(AdaptiveCardActionEvent action)
    {
        if (_store is null)
            return null;

        var customerId = FindText(action.Payload, "customerId");
        var fullName = FindText(action.Payload, "fullName");
        var title = FindText(action.Payload, "title");
        var email = FindText(action.Payload, "email");

        if (string.IsNullOrWhiteSpace(customerId)
            || string.IsNullOrWhiteSpace(fullName)
            || string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return _store.CreateContact(
            customerId,
            fullName,
            title,
            email,
            FindText(action.Payload, "phone") ?? "",
            FindBool(action.Payload, "primary"));
    }

    private static bool IsCustomerViewAction(AdaptiveCardActionEvent action)
        => MatchesAction(action, CrmSurfaceIds.CustomerSelected)
            || MatchesAction(action, CrmSurfaceIds.ContactSelected)
            || MatchesAction(action, CrmSurfaceIds.CustomerView)
            || MatchesAction(action, CrmSurfaceIds.View);

    private static bool MatchesAction(AdaptiveCardActionEvent action, string expected)
        => string.Equals(action.ActionId, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Verb, expected, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAddContactFormRequest(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("contact", StringComparison.Ordinal)
            && (lower.Contains("add", StringComparison.Ordinal)
                || lower.Contains("new", StringComparison.Ordinal)
                || lower.Contains("create", StringComparison.Ordinal));
    }

    private static string? FindCustomerId(string message)
        => CustomerIdRegex.Match(message) is { Success: true } match
            ? match.Value.ToUpperInvariant()
            : null;

    public static string? ExtractRecordId(AdaptiveCardActionEvent action)
        => FindText(action.Payload, "id", "customerId", "contactId", "selectedId")
            ?? FindText(action.Result?.Data, "id", "customerId", "contactId", "selectedId")
            ?? FindKnownRecordId(action.Message);

    private static string? FindText(IReadOnlyDictionary<string, object?>? payload, params string[] keys)
    {
        if (payload is null)
            return null;

        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var direct) && ReadValueText(direct) is { Length: > 0 } text)
                return text;
        }

        foreach (var value in payload.Values)
        {
            if (ReadNestedText(value, keys) is { Length: > 0 } nested)
                return nested;
        }

        return null;
    }

    private static bool FindBool(IReadOnlyDictionary<string, object?>? payload, string key)
        => FindValue(payload, key) switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            { } value => bool.TryParse(value.ToString(), out var parsed) && parsed,
            _ => false
        };

    private static object? FindValue(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null)
            return null;

        if (payload.TryGetValue(key, out var direct))
            return direct;

        foreach (var value in payload.Values)
        {
            if (ReadNestedValue(value, key) is { } nested)
                return nested;
        }

        return null;
    }

    private static string? ReadNestedText(object? value, string[] keys)
    {
        if (value is null)
            return null;

        if (value is JsonElement json)
            return ReadJsonText(json, keys);

        if (value is IReadOnlyDictionary<string, object?> dictionary)
            return FindText(dictionary, keys);

        if (value is IDictionary<string, object?> mutableDictionary)
            return FindText(new Dictionary<string, object?>(mutableDictionary), keys);

        return null;
    }

    private static object? ReadNestedValue(object? value, string key)
    {
        if (value is null)
            return null;

        if (value is JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object)
                return null;

            if (json.TryGetProperty(key, out var property))
                return property;

            foreach (var child in json.EnumerateObject())
            {
                if (ReadNestedValue(child.Value, key) is { } nested)
                    return nested;
            }

            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> dictionary)
            return FindValue(dictionary, key);

        if (value is IDictionary<string, object?> mutableDictionary)
            return FindValue(new Dictionary<string, object?>(mutableDictionary), key);

        return null;
    }

    private static string? ReadJsonText(JsonElement json, string[] keys)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var property) && ReadValueText(property) is { Length: > 0 } text)
                return text;
        }

        foreach (var property in json.EnumerateObject())
        {
            if (ReadJsonText(property.Value, keys) is { Length: > 0 } nested)
                return nested;
        }

        return null;
    }

    private static string? ReadValueText(object? value)
    {
        if (value is null)
            return null;

        return value is JsonElement json
            ? json.ValueKind == JsonValueKind.String ? json.GetString() : json.ToString()
            : value.ToString();
    }

    private static string? FindKnownRecordId(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        foreach (var token in message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = token.Trim('.', ',', ';', ':', '!', '?', '"', '\'');
            if (candidate.StartsWith("CUS-", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("CON-", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
