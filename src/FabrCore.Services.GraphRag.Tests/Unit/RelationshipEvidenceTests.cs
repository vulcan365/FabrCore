using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class RelationshipEvidenceTests
{
    [TestMethod]
    public void SourceIds_AreStableAndMustReferToTheActualBatch()
    {
        var a = SourceSpanPlan.Id("First span");
        var b = SourceSpanPlan.Id("Second span");
        Assert.AreNotEqual(a, b);
        Assert.AreEqual(a, SourceSpanPlan.Id("First span"));
        var allowed = new HashSet<string>(StringComparer.Ordinal) { a };
        Assert.IsTrue(SourceSpanPlan.ValidIds([a], allowed));
        Assert.IsFalse(SourceSpanPlan.ValidIds([b], allowed));
        Assert.IsFalse(SourceSpanPlan.ValidIds([a, b], allowed));
        Assert.IsFalse(SourceSpanPlan.ValidIds([], allowed));
        Assert.IsFalse(SourceSpanPlan.ValidIds(null, allowed));
        StringAssert.Contains(SourceSpanPlan.Label(["First span"])[0], "First span");
    }

    [TestMethod]
    public void Evidence_MustOccurInSourceAndCannotComeFromAnotherBatch()
    {
        var edge = new EvidenceEdge("A", "B", "USES", "A uses B.");
        Assert.IsTrue(RelationshipEvidenceValidator.Validate(["A\n uses B."], ["A", "B"], [edge]).Passed);
        Assert.IsFalse(RelationshipEvidenceValidator.Validate(["A mentions B."], ["A", "B"], [edge]).Passed);
        Assert.IsFalse(RelationshipEvidenceValidator.Validate(["Different batch."], ["A", "B"], [edge]).Passed);
        Assert.IsFalse(RelationshipEvidenceValidator.Validate(["A uses B."], ["A", "B"], [edge with { Evidence = "" }]).Passed);
        Assert.IsFalse(RelationshipEvidenceValidator.Validate(["A uses B."], ["A"], [edge]).Passed);
    }

    [TestMethod]
    public void DirectionChecks_RejectAsymmetricConflictsButAllowReciprocalCitations()
    {
        var edges = new[] { new EvidenceEdge("A", "B", "PUBLISHED_BY", null), new EvidenceEdge("B", "A", "PUBLISHED_BY", null) };
        var result = RelationshipEvidenceValidator.Validate([], ["A", "B"], edges, requireEvidence: false);
        Assert.HasCount(2, result.Issues);
        Assert.IsTrue(result.Issues.All(i => i.Reason == "contradictory-direction"));
        Assert.IsTrue(RelationshipEvidenceValidator.Validate([], ["A", "B"],
            edges.Select(e => e with { Type = "REFERENCES" }).ToArray(), requireEvidence: false).Passed);
    }

    [TestMethod]
    public void VerbatimQuote_IsNotASemanticEntailmentCheck()
    {
        // Explicitly document the limit: text presence cannot infer predicate polarity.
        Assert.IsTrue(RelationshipEvidenceValidator.Validate(["A does not use B."], ["A", "B"],
            [new("A", "B", "USES", "A does not use B.")]).Passed);
    }
}
