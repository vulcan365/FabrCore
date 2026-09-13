using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.Json;

namespace FabrCore.Host.Services;

/// <summary>Preserves custom agent JSON in Orleans' Newtonsoft-based grain storage.</summary>
internal sealed class OrleansJsonElementConverter : JsonConverter<JsonElement>
{
    public override void WriteJson(JsonWriter writer, JsonElement value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            writer.WriteNull();
        else
            writer.WriteRawValue(value.GetRawText());
    }

    public override JsonElement ReadJson(JsonReader reader, Type objectType, JsonElement existingValue,
        bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        using var document = JsonDocument.Parse(token.ToString(Formatting.None));
        return document.RootElement.Clone();
    }
}
