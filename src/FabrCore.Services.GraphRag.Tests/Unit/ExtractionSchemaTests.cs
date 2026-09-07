using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.EvalConsole;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class ExtractionSchemaTests
{
    [TestMethod]
    public void PolicyObligation_ParsesStoresAndRestoresWithoutChangingFactDescriptions()
    {
        foreach (var value in PolicyObligation.Values)
        {
            var type = value == "prohibited" ? "PROHIBITS" : "USES";
            var json = $$"""
                {"entities":[],"relationships":[{"from":"Agency","to":"Format","type":"{{type}}","description":"Original source wording.","obligation":"{{value}}"}]}
                """;
            var stored = KnowledgeIngestionService.ParsePolicyDescriptionsForTesting(json).Single();
            var parsed = PolicyObligation.Read(stored);
            Assert.AreEqual(value, parsed.Obligation);
            Assert.AreEqual("Original source wording.", parsed.Description);
            var label = new FactualEdgeLabel("required", "must use", "source", "source",
                "^Agency$", ["USES"], "^Format$", DescriptionPattern: "required|must");
            Assert.IsFalse(FactualEdgeCheck.Evaluate(label,
                [new("Agency", "USES", "Format", parsed.Description) { Obligation = parsed.Obligation }], "source").Passed);
        }
        Assert.AreEqual(("legacy", (string?)null), PolicyObligation.Read("legacy"));
        Assert.IsNull(PolicyObligation.Store(null, null));
    }

    [TestMethod]
    public void PolicyObligation_RejectsMissingInvalidAndContradictoryMetadata()
    {
        foreach (var (type, field) in new[] {
            ("USES", ""), ("USES", ",\"obligation\":\"unknown\""),
            ("USES", ",\"obligation\":\"prohibited\""),
            ("PROHIBITS", ",\"obligation\":\"none\""),
            ("REQUIRES", ",\"obligation\":\"required\"") })
        {
            var json = $$"""
                {"entities":[],"relationships":[{"from":"Agency","to":"Format","type":"{{type}}","description":"text"{{field}}}]}
                """;
            Assert.Throws<JsonException>(() => KnowledgeIngestionService.ParsePolicyDescriptionsForTesting(json));
        }
        var schema = ((ChatResponseFormatJson)ExtractionResponseSchema.Create(ExtractionResponseKind.Graph,
            policyRelations: true, policyObligations: true)).Schema!.Value;
        var edge = schema.GetProperty("properties").GetProperty("relationships").GetProperty("items");
        CollectionAssert.Contains(edge.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ToArray(), "obligation");
        var types = edge.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(p => p.GetString()).ToArray();
        CollectionAssert.DoesNotContain(types, "REQUIRES");
        CollectionAssert.Contains(types, "USES");
    }

    [TestMethod]
    public void PolicyObligationChecks_RejectRecommendationUpgradesAndReversedActors()
    {
        var label = new FactualEdgeLabel("recommendation", "Agency should use format", "source", "Agency should use format",
            "^Agency$", ["USES"], "^Format$", DescriptionPattern: "should|recommended");
        var good = new EdgeSnapshot("Agency", "USES", "Format", "Agency should use Format where permitted by law.");
        Assert.IsTrue(FactualEdgeCheck.Evaluate(label, [good], "Agency should use format").Consistent);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [good with { From = "Format", To = "Agency" }], "Agency should use format").Consistent);
        var upgraded = good with { Description = "Agency must use Format." };
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [upgraded], "Agency should use format").Passed);
        var negative = label with { Forbidden = true, DescriptionPattern = "must|required|mandatory" };
        Assert.IsFalse(FactualEdgeCheck.Evaluate(negative, [upgraded], "Agency should use format").Passed);
        Assert.IsTrue(FactualEdgeCheck.Evaluate(negative, [good], "Agency should use format").Passed);
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--relations", "policy"]));
    }

    [TestMethod]
    public void StructuredTaxonomySchema_ConstrainsNameGuidanceWithoutChangingGraphSchema()
    {
        foreach (var kind in new[] { ExtractionResponseKind.Combined, ExtractionResponseKind.Taxonomy })
        {
            var format = (ChatResponseFormatJson)ExtractionResponseSchema.Create(kind, structuredTaxonomyNames: true);
            var properties = format.Schema!.Value.GetProperty("properties");
            foreach (var label in new[] { "domain", "category" })
                StringAssert.Contains(properties.GetProperty(label).GetProperty("properties").GetProperty("name")
                    .GetProperty("description").GetString()!, "copy an existing name value exactly");
        }
        var current = (ChatResponseFormatJson)ExtractionResponseSchema.Create(ExtractionResponseKind.Graph);
        var structured = (ChatResponseFormatJson)ExtractionResponseSchema.Create(ExtractionResponseKind.Graph, structuredTaxonomyNames: true);
        Assert.AreEqual(current.Schema!.Value.GetRawText(), structured.Schema!.Value.GetRawText());
    }

    [TestMethod]
    public async Task AzureAdapter_SendsStrictSchemaAndCompactGuidance()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIClient(new Uri("https://unit-test.openai.azure.com"), new ApiKeyCredential("test"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) })
            .GetChatClient("test-model").AsIChatClient();
        foreach (var kind in Enum.GetValues<ExtractionResponseKind>())
        {
            await client.GetResponseAsync("Extract JSON", new ChatOptions
            {
                ResponseFormat = ExtractionResponseSchema.Create(kind, 120, includeEvidence: true, includeSourceIds: true, allowedSourceIds: ["s_known"]),
                AdditionalProperties = new() { ["strict"] = true }, MaxOutputTokens = 1000
            });
            using var request = JsonDocument.Parse(handler.Body!);
            var format = request.RootElement.GetProperty("response_format");
            Assert.AreEqual("json_schema", format.GetProperty("type").GetString());
            var jsonSchema = format.GetProperty("json_schema");
            Assert.IsTrue(jsonSchema.GetProperty("strict").GetBoolean());
            var schema = jsonSchema.GetProperty("schema");
            Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
            var props = schema.GetProperty("properties");
            Assert.AreEqual(kind == ExtractionResponseKind.EndpointRepair, props.TryGetProperty("unresolvedNames", out _));
            Assert.AreEqual(kind is ExtractionResponseKind.Combined or ExtractionResponseKind.Taxonomy, props.TryGetProperty("domain", out _));
            Assert.AreEqual(kind != ExtractionResponseKind.Taxonomy, props.TryGetProperty("entities", out _));
            Assert.AreEqual(kind is ExtractionResponseKind.Combined or ExtractionResponseKind.Graph, props.TryGetProperty("relationships", out _));
            if (kind is ExtractionResponseKind.Combined or ExtractionResponseKind.Graph)
                Assert.AreEqual("string", props.GetProperty("relationships").GetProperty("items").GetProperty("properties").GetProperty("evidence").GetProperty("type").GetString());
            if (kind is ExtractionResponseKind.Combined or ExtractionResponseKind.Graph)
                Assert.AreEqual("s_known", props.GetProperty("relationships").GetProperty("items").GetProperty("properties").GetProperty("sourceIds").GetProperty("items").GetProperty("enum")[0].GetString());
            var item = kind == ExtractionResponseKind.Taxonomy ? props.GetProperty("domain")
                : props.GetProperty("entities").GetProperty("items");
            StringAssert.Contains(item.GetProperty("properties").GetProperty("description").GetProperty("description").GetString()!, "at most 120 characters");
            Assert.AreEqual(item.GetProperty("properties").EnumerateObject().Count(), item.GetProperty("required").GetArrayLength());
        }
    }

    [TestMethod]
    public void FactualChecks_RejectReversedDirectionWrongTypeAndMissingEvidence()
    {
        var label = new FactualEdgeLabel("publisher", "Standard is authored by organization", "references", "source evidence",
            "^standard$", ["AUTHORED_BY"], "^organization$");
        var correct = new EdgeSnapshot("standard", "AUTHORED_BY", "organization", "Published by organization");
        Assert.IsTrue(FactualEdgeCheck.Evaluate(label, [correct], "source\n evidence").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [correct with { From = "organization", To = "standard" }], "source evidence").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [correct with { Type = "RELATED_TO" }], "source evidence").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [correct], "unrelated source").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [], "source evidence").Passed);
        var synonym = new FactualEdgeLabel("synonym", "UUID also known as GUID", "introduction", "also known as",
            "UUID", ["RELATED_TO"], "GUID", true, "also known|equivalent|same|synonym");
        Assert.IsFalse(FactualEdgeCheck.Evaluate(synonym,
            [new("UUID format", "RELATED_TO", "GUIDs", "COM GUIDs have a storage caveat")], "also known as").Passed);
        Assert.IsTrue(FactualEdgeCheck.Evaluate(synonym,
            [new("GUID", "RELATED_TO", "UUID", "UUIDs are also known as GUIDs")], "also known as").Passed);
    }

    [TestMethod]
    public void FactDiagnostics_DoNotPromoteAlternateOrForbiddenEdges()
    {
        var label = new FactualEdgeLabel("dependency", "format depends on encoding", "section", "evidence",
            "^Format$", ["DEPENDS_ON"], "^Encoding$", EvidencePattern: "Format.*Encoding");
        var alternate = new EdgeSnapshot("Specification", "ESTABLISHES", "Encoding", "Format uses Encoding");
        var result = FactualEdgeCheck.Evaluate(label, [alternate], "evidence");
        Assert.IsFalse(result.Passed);
        Assert.HasCount(1, result.OtherRepresentations);
        var contradictory = FactualEdgeCheck.Evaluate(label,
            [new("Format", "DEPENDS_ON", "Encoding", "Required"), new("Encoding", "DEPENDS_ON", "Format", "Required")], "evidence");
        Assert.IsTrue(contradictory.Passed); // Positive coverage alone is not consistency.
        Assert.IsFalse(contradictory.Consistent);
        Assert.HasCount(1, contradictory.DirectionConflicts);
        var forbidden = label with { Forbidden = true };
        Assert.IsTrue(FactualEdgeCheck.Evaluate(forbidden, [], "evidence").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(forbidden, [], "missing source").Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(forbidden,
            [new("Format", "DEPENDS_ON", "Encoding", "Required")], "evidence").Passed);
        Assert.ThrowsExactly<ArgumentException>(() => Options.Parse(["run", "--relations", "unknown"]));
    }

    [TestMethod]
    public void RelationManifest_AcceptsAliasParaphraseButRejectsStorageAssociation()
    {
        var entries = JsonSerializer.Deserialize<List<CorpusEntry>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus-relations-v2.json")))!;
        var label = entries.SelectMany(e => e.ExpectedEdges!).Single(f => f.Id == "uuid-guid");
        Assert.IsTrue(FactualEdgeCheck.Evaluate(label,
            [new("GUID", "ALIAS_OF", "UUID", "GUID is stated to be another name for UUID.")], label.EvidenceText).Passed);
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label,
            [new("UUID format", "RELATED_TO", "GUIDs", "COM GUIDs have a storage caveat")], label.EvidenceText).Passed);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"test","object":"chat.completion","created":1,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"{}"},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
