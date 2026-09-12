namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Explicit, ordered registry of every GraphRAG schema migration. Add new
/// migrations by creating a class implementing <see cref="IGraphRagMigration"/>
/// in this folder and appending it to <see cref="Registered"/> below.
///
/// <para>
/// We deliberately do not use reflection-based discovery so additions are
/// visible in PR review and the order is unambiguous in source.
/// </para>
/// </summary>
internal static class Migrations
{
    /// <summary>
    /// All GraphRAG migrations, in the order they were authored. Order does
    /// not have to match <see cref="IGraphRagMigration.Version"/> ordering
    /// (the runner sorts before applying), but keeping them aligned makes the
    /// list easier to read.
    /// </summary>
    public static readonly IGraphRagMigration[] Registered =
    [
        new M001_BaselineSchema(),
        new M002_ActionAudit(),
        new M003_SourceDocumentMetadata(),
        new M004_SourceDocumentRuntimeColumns(),
        new M005_ScopedCanonicalKnowledge(),
        new M006_SourceDocumentInstructionHash(),
        new M007_IngestionPerformanceMetrics(),
        new M008_ExtractionBatchMetrics(),
    ];
}
