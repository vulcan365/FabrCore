using FabrCore.Services.GraphRag.Migrations;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class SchemaContractTests
{
    [TestMethod]
    public void RegisteredMigrations_AreOrderedUniqueAndCurrent()
    {
        var versions = FabrCore.Services.GraphRag.Migrations.Migrations.Registered.Select(m => m.Version).ToArray();

        CollectionAssert.AreEqual(versions.Order().ToArray(), versions);
        Assert.AreEqual(versions.Length, versions.Distinct().Count());
        Assert.AreEqual(8L, versions[^1]);
    }

    [TestMethod]
    public void CoreSchema_UsesGraphNodesEdgesAnd1536DimensionVectors()
    {
        StringAssert.Contains(GraphRagSchemaInitializer.GetKnowledgeEntityDdl(), "AS NODE");
        StringAssert.Contains(GraphRagSchemaInitializer.GetKnowledgeEntityDdl(), "Embedding VECTOR(1536)");
        StringAssert.Contains(GraphRagSchemaInitializer.GetKnowledgeRelationshipDdl(), "AS EDGE");
        StringAssert.Contains(GraphRagSchemaInitializer.GetKnowledgeRelationshipDdl(), "ScopeKey NVARCHAR(200) NOT NULL");
        StringAssert.Contains(GraphRagSchemaInitializer.GetKnowledgeChunkDdl(), "Embedding VECTOR(1536)");
    }

    [TestMethod]
    public void ProvenanceAndSourceIdentity_HaveRequiredConstraintsAndIndexes()
    {
        StringAssert.Contains(GraphRagSchemaInitializer.GetDocumentContributionDdl(), "ON DELETE CASCADE");
        StringAssert.Contains(GraphRagSchemaInitializer.GetSourceDocumentIndexesDdl(), "UX_SourceDocument_Scope_Source");
        StringAssert.Contains(GraphRagSchemaInitializer.GetIndexesDdl(), "IX_KnowledgeEntity_Name_Type_Scope");
    }
}
