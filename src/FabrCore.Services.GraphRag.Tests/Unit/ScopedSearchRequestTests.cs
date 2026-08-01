using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class ScopedSearchRequestTests
{
    [TestMethod]
    public void Validate_AcceptsBoundaryLimitsAndMultipleScopes()
    {
        new ScopedSearchRequest("database migration", ["team-a", "shared"], 1).Validate();
        new ScopedSearchRequest("database migration", ["team-a", "shared"], 200).Validate();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Validate_RejectsMissingQuery(string? query)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScopedSearchRequest(query!, ["allowed"]).Validate());
    }

    [TestMethod]
    public void Validate_RejectsMissingOrBlankScopes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ScopedSearchRequest("query", []).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new ScopedSearchRequest("query", ["allowed", " "]).Validate());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(201)]
    public void Validate_RejectsOutOfRangeLimit(int limit)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScopedSearchRequest("query", ["allowed"], limit).Validate());
    }

    [TestMethod]
    public void RelationshipValidate_EnforcesDepthAndPinnedSourceScope()
    {
        new ScopedRelationshipRequest("Apollo", "Project", ["private", "shared"], Depth: 3, SourceScope: "private").Validate();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ScopedRelationshipRequest("Apollo", "Project", ["private"], Depth: 4).Validate());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScopedRelationshipRequest("Apollo", "Project", ["private"], SourceScope: "shared").Validate());
    }
}
