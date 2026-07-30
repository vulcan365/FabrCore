using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// A single forward-only schema migration for the GraphRAG <c>grag.*</c>
/// database. Each migration runs at most once per database. Applied versions
/// are recorded in <c>grag.SchemaVersion</c> by <see cref="GraphRagMigrationRunner"/>.
///
/// <para><b>Authoring rules</b></para>
/// <list type="bullet">
///   <item><see cref="Version"/> values are monotonically increasing and unique
///   across the project. Never reuse, reorder, or delete a version once it has
///   shipped — a migration that has run on any production database must keep
///   its version number forever.</item>
///   <item>Migration bodies must be <b>retry-safe</b>. The runner re-runs a
///   migration on the next startup if it fails, so use idempotent guards
///   (<c>IF COL_LENGTH</c>, <c>IF NOT EXISTS</c>, <c>IF OBJECT_ID(...) IS NULL</c>)
///   inside <see cref="ApplyAsync"/> wherever practical.</item>
///   <item>Keep one logical change per migration. "Add column" + "backfill"
///   should generally be split into M00N and M00N+1 when the backfill is large.</item>
///   <item>The runner gives every migration its own SQL transaction. All
///   <see cref="SqlCommand"/>s constructed inside <see cref="ApplyAsync"/> must
///   be tagged with the supplied <see cref="SqlTransaction"/>; otherwise SQL
///   Server will error.</item>
///   <item><c>grag.SchemaVersion</c> itself is bootstrapped outside the
///   migration system and must never be modified by a migration.</item>
/// </list>
/// </summary>
public interface IGraphRagMigration
{
    /// <summary>
    /// Monotonically increasing version number. Acts as the primary key in
    /// <c>grag.SchemaVersion</c>. The first migration is <c>1</c>.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// Human-readable description recorded alongside the version number. Shown
    /// in startup logs and any future admin UI.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Apply the migration against the supplied open connection and
    /// transaction. The runner wraps the call in a transaction and inserts the
    /// <c>grag.SchemaVersion</c> row on successful return; throwing causes the
    /// transaction to roll back and startup to fail.
    /// </summary>
    Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger);
}
