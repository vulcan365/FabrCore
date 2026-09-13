using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Connections;

internal static class ConnectionResponse
{
    internal static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(ct))!;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String)
                throw new ConnectionException(code.GetString()!, root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()! : "The connection operation was rejected.");
        }
        catch (JsonException) { }
        throw new ConnectionException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "authentication-required" : "request-failed",
            $"The connection API returned {(int)response.StatusCode}.");
    }
}
