using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Resolves the global, non-authoritative identity shared by scoped entity
/// views. Descriptions, content, embeddings, relationships, and taxonomy
/// assignments remain on scope-owned rows.
/// </summary>
internal static class CanonicalEntityStore
{
    public static async Task<Guid> GetOrCreateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string name,
        string entityType,
        CancellationToken ct = default)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;
        var sql = $"""
            MERGE {schema}.CanonicalEntity WITH (HOLDLOCK) AS target
            USING (SELECT @name AS Name, @entityType AS EntityType) AS source
            ON target.Name = source.Name AND target.EntityType = source.EntityType
            WHEN MATCHED THEN
                UPDATE SET UpdatedAt = target.UpdatedAt
            WHEN NOT MATCHED THEN
                INSERT (CanonicalEntityId, Name, EntityType)
                VALUES (NEWID(), @name, @entityType)
            OUTPUT INSERTED.CanonicalEntityId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@entityType", entityType);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }
}
