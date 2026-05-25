using System.Text.Json;

namespace FabrCore.Surface;

public static class SurfaceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
