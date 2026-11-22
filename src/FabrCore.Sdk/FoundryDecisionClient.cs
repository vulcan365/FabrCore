using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FabrCore.Sdk;

/// <summary>Calls a named deployment of Microsoft's Foundry Decision API.</summary>
public interface IFabrCoreDecisionClient
{
    /// <summary>Evaluate named noul, choice or score questions over a shared state and optional image assets.</summary>
    Task<JsonObject> DecideAsync(JsonNode state, JsonObject questions, JsonObject? assets = null,
        CancellationToken cancellationToken = default);
}

internal sealed class FoundryDecisionClient(HttpClient http, Uri endpoint, string deployment, string key,
    TimeSpan timeout, bool emitAttributionHeaders) : IFabrCoreDecisionClient
{
    public async Task<JsonObject> DecideAsync(JsonNode state, JsonObject questions, JsonObject? assets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
            throw new ArgumentException("Supply at least one named decision question.", nameof(questions));

        var payload = new Dictionary<string, object> { ["model"] = deployment, ["state"] = state, ["questions"] = questions };
        if (assets is not null) payload["assets"] = assets;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(payload) };
        request.Headers.Add("api-key", key);
        if (emitAttributionHeaders)
            AgentAttributionPipelinePolicy.ApplyHeaders((name, value) => request.Headers.Add(name, value));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(deadline.Token).ConfigureAwait(false);
        return result?["answers"] is JsonObject ? result : throw new InvalidDataException("Foundry returned no decision answers.");
    }
}
