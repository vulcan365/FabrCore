using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.EvalConsole;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class ExtractionEndpointResolverTests
{
    [TestMethod]
    public void ResolvesOnlyExplicitUnambiguousInitialisms()
    {
        var resolver = new ExtractionEndpointResolver(["Internet Assigned Numbers Authority (IANA)",
            "Universally Unique IDentifiers (UUIDs)", "JSON (JavaScript Object Notation)", "RFC 4180 (CSV)"]);
        Assert.AreEqual("Internet Assigned Numbers Authority (IANA)", resolver.Resolve("IANA"));
        Assert.AreEqual("Universally Unique IDentifiers (UUIDs)", resolver.Resolve("UUIDs"));
        Assert.AreEqual("JSON (JavaScript Object Notation)", resolver.Resolve("JSON"));
        Assert.AreEqual("UUID", resolver.Resolve("UUID"));
        Assert.AreEqual("JSON text", resolver.Resolve("JSON text"));
        Assert.AreEqual("CSV", resolver.Resolve("CSV"));
        Assert.AreEqual("Unknown", resolver.Resolve("Unknown"));
    }

    [TestMethod]
    public void ExactNamesAndAmbiguousInitialismsAreNotRewritten()
    {
        var exact = new ExtractionEndpointResolver(["IANA", "Internet Assigned Numbers Authority (IANA)"]);
        Assert.AreEqual("IANA", exact.Resolve("IANA"));
        var ambiguous = new ExtractionEndpointResolver(["Artificial Intelligence (AI)", "Application Interface (AI)"]);
        Assert.AreEqual("AI", ambiguous.Resolve("AI"));
    }

    [TestMethod]
    public void TraceDistinguishesMissingEndpointsAndAlternatePredicates()
    {
        var label = new FactualEdgeLabel("test", "IANA registers text/csv", "4", "registered", "^IANA$", ["ESTABLISHES"], "^text/csv$");
        var edge = new EdgeSnapshot("IANA", "ESTABLISHES", "text/csv", "registered");
        var trace = ExtractionTrace.Evaluate(label, "registered", ["Internet Assigned Numbers Authority (IANA)", "text/csv"], [edge], [], true);
        Assert.AreEqual("raw-match-missing-endpoint", trace.Outcome);
        CollectionAssert.AreEqual(new[] { "IANA" }, trace.MissingEndpointNames);
        Assert.AreEqual("alternative-representation", ExtractionTrace.Evaluate(label, "registered", ["IANA", "text/csv"],
            [edge with { Type = "RELATED_TO" }], [], true).Outcome);
        Assert.AreEqual("raw-unavailable", ExtractionTrace.Evaluate(label, "registered", [], [], [], false).Outcome);
    }

    [TestMethod]
    public void StrictJsonIdentityDoesNotCreditAParserAsTheFormat()
    {
        var label = new FactualEdgeLabel("json", "encoding", "8.1", "UTF-8", "^(?:JSON|JSON text)$", ["USES"], "^UTF-8$");
        Assert.IsFalse(FactualEdgeCheck.Evaluate(label, [new("JSON parser", "USES", "UTF-8", "encoding")], "UTF-8").Passed);
        Assert.IsTrue(FactualEdgeCheck.Evaluate(label, [new("JSON", "USES", "UTF-8", "encoding")], "UTF-8").Passed);
    }
}
