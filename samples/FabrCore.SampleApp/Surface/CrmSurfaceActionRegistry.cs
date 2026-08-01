using System.Text.Json;
using FabrCore.SampleApp.Crm;
using FabrCore.Surface.Actions;

namespace FabrCore.SampleApp.Surface;

public sealed class CrmSurfaceActionRegistry(InMemoryCrmStore store) : ISurfaceActionRegistry
{
    public Task<SurfaceActionResult> ExecuteAsync(SurfaceActionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var action = request.ActionId;
            var result = action switch
            {
                CrmSurfaceIds.CreateCustomer => CreateCustomer(request.Payload),
                CrmSurfaceIds.CreateContact => CreateContact(request.Payload),
                CrmSurfaceIds.UpdateCustomerStatus => UpdateCustomerStatus(request.Payload),
                CrmSurfaceIds.CustomerSelected or CrmSurfaceIds.ContactSelected or CrmSurfaceIds.View => Selection(request.Payload),
                _ => new SurfaceActionResult { Success = false, Message = $"Unknown CRM action '{action}'." }
            };

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SurfaceActionResult
            {
                Success = false,
                Message = ex.Message
            });
        }
    }

    private SurfaceActionResult CreateCustomer(IReadOnlyDictionary<string, object?> payload)
    {
        var customer = store.CreateCustomer(
            Required(payload, "name"),
            Required(payload, "segment"),
            Required(payload, "owner"),
            Text(payload, "status") ?? "Prospect",
            Decimal(payload, "annualRevenue"),
            Text(payload, "notes"));

        return Success($"Created customer {customer.Name}.", ("customerId", customer.Id), ("name", customer.Name));
    }

    private SurfaceActionResult CreateContact(IReadOnlyDictionary<string, object?> payload)
    {
        var contact = store.CreateContact(
            Required(payload, "customerId"),
            Required(payload, "fullName"),
            Required(payload, "title"),
            Required(payload, "email"),
            Text(payload, "phone") ?? "",
            Bool(payload, "primary"));

        return Success($"Created contact {contact.FullName}.", ("contactId", contact.Id), ("customerId", contact.CustomerId));
    }

    private SurfaceActionResult UpdateCustomerStatus(IReadOnlyDictionary<string, object?> payload)
    {
        var customer = store.UpdateCustomerStatus(Required(payload, "customerId"), Required(payload, "status"));
        return Success($"Updated {customer.Name} to {customer.Status}.", ("customerId", customer.Id), ("status", customer.Status));
    }

    private static SurfaceActionResult Selection(IReadOnlyDictionary<string, object?> payload)
        => Success("Selection received.", ("selectedId", Text(payload, "id") ?? Text(payload, "customerId") ?? Text(payload, "contactId") ?? ""));

    private static SurfaceActionResult Success(string message, params (string Key, object? Value)[] data)
        => new()
        {
            Success = true,
            Message = message,
            Data = data.ToDictionary(v => v.Key, v => v.Value)
        };

    private static string Required(IReadOnlyDictionary<string, object?> payload, string key)
        => Text(payload, key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"'{key}' is required.");

    private static string? Text(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            if (payload.TryGetValue("inputs", out var inputs))
                return NestedText(inputs, key);

            if (payload.TryGetValue("values", out var values))
                return NestedText(values, key);

            return null;
        }

        if (value is JsonElement json)
            return json.ValueKind == JsonValueKind.String ? json.GetString() : json.ToString();

        return value.ToString();
    }

    private static decimal Decimal(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var text = Text(payload, key);
        return decimal.TryParse(text, out var value) ? value : 0m;
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            if (payload.TryGetValue("inputs", out var inputs))
                return bool.TryParse(NestedText(inputs, key), out var input) && input;

            if (payload.TryGetValue("values", out var values))
                return bool.TryParse(NestedText(values, key), out var nested) && nested;

            return false;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => bool.TryParse(value.ToString(), out var parsed) && parsed
        };
    }

    private static string? FirstText(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NestedText(object? value, string key)
    {
        if (value is null)
            return null;

        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            return json.TryGetProperty(key, out var property)
                ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
                : null;
        }

        if (value is IReadOnlyDictionary<string, object?> dictionary)
            return Text(dictionary, key);

        if (value is IDictionary<string, object?> mutableDictionary)
            return Text(new Dictionary<string, object?>(mutableDictionary), key);

        return null;
    }
}
