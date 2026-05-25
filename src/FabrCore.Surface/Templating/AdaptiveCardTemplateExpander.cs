using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FabrCore.Surface.Templating;

public static partial class AdaptiveCardTemplateExpander
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonElement Expand(JsonElement cardTemplate, JsonElement? data)
    {
        if (data is null || data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return cardTemplate.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteExpandedValue(writer, cardTemplate, data.Value);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteExpandedValue(Utf8JsonWriter writer, JsonElement value, JsonElement data)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteExpandedValue(writer, property.Value, data);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteExpandedValue(writer, item, data);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                WriteExpandedString(writer, value.GetString() ?? string.Empty, data);
                break;

            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteExpandedString(Utf8JsonWriter writer, string template, JsonElement data)
    {
        var wholeValue = WholeBindingRegex().Match(template);
        if (wholeValue.Success && TryGetPath(data, wholeValue.Groups["path"].Value, out var bound))
        {
            bound.WriteTo(writer);
            return;
        }

        var expanded = BindingRegex().Replace(
            template,
            match => TryGetPath(data, match.Groups["path"].Value, out var boundValue)
                ? ValueToString(boundValue)
                : match.Value);

        writer.WriteStringValue(expanded);
    }

    private static bool TryGetPath(JsonElement root, string path, out JsonElement value)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static string ValueToString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => string.Empty,
            _ => JsonSerializer.Serialize(value, WriteOptions)
        };

    [GeneratedRegex(@"^\$\{(?<path>[A-Za-z0-9_.-]+)\}$")]
    private static partial Regex WholeBindingRegex();

    [GeneratedRegex(@"\$\{(?<path>[A-Za-z0-9_.-]+)\}")]
    private static partial Regex BindingRegex();
}
