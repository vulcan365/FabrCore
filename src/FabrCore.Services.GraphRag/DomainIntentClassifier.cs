using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Lightweight query-time classifier that detects which knowledge domain a
/// user question relates to. Fetches existing domains WITH their descriptions
/// so the LLM can route by subject area rather than by lexical match against
/// raw names — "Equipment" means different things in two different knowledge
/// bases, and only the description disambiguates it.
///
/// The classifier returns a primary/secondary domain and a flag indicating
/// whether the query is broad enough to warrant community-summary search.
/// </summary>
internal class DomainIntentClassifier
{
    private readonly IChatClient _chatClient;
    private readonly GraphRagDomainPlugin _domainPlugin;
    private readonly ILogger _logger;

    // Cached domain list with TTL. Cache now holds name + description so
    // description changes propagate on the next 5-minute refresh. Per-
    // instance cache; no cross-instance invalidation needed.
    private List<DomainSummary> _cachedDomains = [];
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public DomainIntentClassifier(
        IChatClient chatClient,
        GraphRagDomainPlugin domainPlugin,
        ILogger logger)
    {
        _chatClient = chatClient;
        _domainPlugin = domainPlugin;
        _logger = logger;
    }

    public async Task<DomainClassification> ClassifyQueryAsync(string userQuery)
    {
        try
        {
            var domains = await GetDomainsAsync();

            if (domains.Count == 0)
            {
                _logger.LogDebug("No domains defined — skipping intent classification");
                return DomainClassification.None;
            }

            var prompt = BuildClassificationPrompt(userQuery, domains);

            var response = await _chatClient.GetResponseAsync(prompt);
            var text = response.Text ?? "";

            return ParseClassification(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Domain intent classification failed for query, proceeding without domain filter");
            return DomainClassification.None;
        }
    }

    private async Task<List<DomainSummary>> GetDomainsAsync()
    {
        if (DateTime.UtcNow < _cacheExpiry && _cachedDomains.Count > 0)
            return _cachedDomains;

        _cachedDomains = await _domainPlugin.GetDomainsWithDescriptionsAsync();
        _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
        return _cachedDomains;
    }

    private static string BuildClassificationPrompt(string userQuery, List<DomainSummary> domains)
    {
        var block = new StringBuilder();
        block.AppendLine("Existing knowledge domains:");
        foreach (var dom in domains)
        {
            var desc = string.IsNullOrWhiteSpace(dom.Description)
                ? "(no description yet — do not infer subject area from the name alone)"
                : dom.Description.Trim();
            block.Append("- ").Append(dom.Name).Append(": ").AppendLine(desc);
        }

        return $$"""
            Classify the following user question by matching its intent against
            each knowledge domain's description below. Name alone is not enough —
            two domains can share a lexically similar name but describe different
            subject areas. Pick the domain whose description best fits the user's
            question. If no description fits, primaryDomain must be null.

            {{block.ToString().TrimEnd()}}

            Return ONLY a JSON object with this exact structure — no other text:
            {"primaryDomain":"...","secondaryDomain":null,"confidence":0.9,"isBroadQuery":false}

            Rules:
            - primaryDomain: the domain whose description best matches the query
              (must be one of the names listed above, or null if none fit)
            - secondaryDomain: a second relevant domain when the question spans
              multiple areas, or null
            - confidence: 0.0-1.0 how confident you are in the primary domain
            - isBroadQuery: true if the question is general/overview (e.g., "tell me
              about our policies"), false if specific

            User question: {{userQuery}}
            """;
    }

    private DomainClassification ParseClassification(string response)
    {
        try
        {
            var trimmed = response.Trim();

            // Strip markdown fences if present
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline > 0)
                    trimmed = trimmed[(firstNewline + 1)..];
                if (trimmed.EndsWith("```"))
                    trimmed = trimmed[..^3].Trim();
            }

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
                return DomainClassification.None;

            var json = trimmed[start..(end + 1)];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var primaryDomain = root.TryGetProperty("primaryDomain", out var pd)
                ? pd.ValueKind == JsonValueKind.Null ? null : pd.GetString()
                : null;

            var secondaryDomain = root.TryGetProperty("secondaryDomain", out var sd)
                ? sd.ValueKind == JsonValueKind.Null ? null : sd.GetString()
                : null;

            var confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.0;
            var isBroadQuery = root.TryGetProperty("isBroadQuery", out var bq) && bq.GetBoolean();

            _logger.LogDebug("Domain classification: primary={Primary}, secondary={Secondary}, confidence={Confidence}, broad={Broad}",
                primaryDomain, secondaryDomain, confidence, isBroadQuery);

            return new DomainClassification(primaryDomain, secondaryDomain, confidence, isBroadQuery);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse domain classification response");
            return DomainClassification.None;
        }
    }
}

/// <summary>
/// Result of domain intent classification for a user query.
/// </summary>
internal record DomainClassification(
    string? PrimaryDomain,
    string? SecondaryDomain,
    double Confidence,
    bool IsBroadQuery)
{
    public static readonly DomainClassification None = new(null, null, 0.0, false);
}
