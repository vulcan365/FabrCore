using Orleans;
using System.Text.Json;

namespace FabrCore.Core
{
    /// <summary>
    /// Orleans surrogate for System.Text.Json.JsonElement serialization.
    /// </summary>
    [GenerateSerializer]
    internal struct JsonElementSurrogate
    {
        [Id(0)]
        public string Json { get; set; }
    }

    /// <summary>
    /// Converter between JsonElement and its Orleans-serializable surrogate.
    /// </summary>
    [RegisterConverter]
    internal sealed class JsonElementSurrogateConverter : IConverter<JsonElement, JsonElementSurrogate>
    {
        public JsonElement ConvertFromSurrogate(in JsonElementSurrogate surrogate)
        {
            if (string.IsNullOrEmpty(surrogate.Json))
            {
                return default;
            }
            return JsonDocument.Parse(surrogate.Json).RootElement.Clone();
        }

        public JsonElementSurrogate ConvertToSurrogate(in JsonElement value)
        {
            return new JsonElementSurrogate { Json = value.GetRawText() };
        }
    }
}
