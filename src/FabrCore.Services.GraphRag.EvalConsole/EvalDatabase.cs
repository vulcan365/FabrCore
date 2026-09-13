using Microsoft.Data.SqlClient;
using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal static class EvalDatabase
{
    public static async Task<List<ChunkVectorFingerprint>> ChunkVectorsAsync(string connectionString, Guid documentId, string scope, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT c.ChunkIndex, c.Content, CAST(c.Embedding AS NVARCHAR(MAX))
            FROM grag.KnowledgeChunk c JOIN grag.SourceDocument d ON d.EntityId=c.EntityId
            WHERE d.DocumentId=@doc AND c.ScopeKey=@scope ORDER BY c.ChunkIndex;
            """, connection);
        command.Parameters.AddWithValue("@doc", documentId);
        command.Parameters.AddWithValue("@scope", scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ChunkVectorFingerprint>();
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetInt32(0), Hash(reader.GetString(1)), Hash(reader.GetString(2))));
        return result;
    }

    public static async Task<int> TaxonomyCountAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT (SELECT COUNT(*) FROM grag.KnowledgeDomain) + (SELECT COUNT(*) FROM grag.KnowledgeCategory)", connection);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public static async Task<Dictionary<string, object?>> MetricsAsync(string connectionString, Guid documentId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT TOP(1) * FROM grag.IngestionMetric WHERE DocumentId=@doc ORDER BY VersionNumber DESC", connection);
        command.Parameters.AddWithValue("@doc", documentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, object?>();
        if (await reader.ReadAsync(ct))
            for (var i = 0; i < reader.FieldCount; i++) result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return result;
    }

    public static async Task<GraphSnapshot> GraphAsync(string connectionString, Guid documentId, string scope, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT e.Name FROM grag.KnowledgeEntity e
            JOIN grag.DocumentContribution dc ON dc.EntityId=e.EntityId AND dc.ItemKind=1
            WHERE dc.DocumentId=@doc AND e.ScopeKey=@scope;
            SELECT f.Name, r.RelationshipType, t.Name, r.Description
            FROM grag.DocumentContribution dc
            JOIN grag.KnowledgeEntity f ON f.EntityId=dc.RelFromEntityId AND f.ScopeKey=@scope
            JOIN grag.KnowledgeEntity t ON t.EntityId=dc.RelToEntityId AND t.ScopeKey=@scope
            JOIN grag.KnowledgeRelationship r ON r.$from_id=f.$node_id AND r.$to_id=t.$node_id
                AND r.RelationshipType=dc.RelationshipType AND r.ScopeKey=@scope
            WHERE dc.DocumentId=@doc AND dc.ItemKind=2 AND r.RelationshipType<>'EXTRACTED_FROM';
            SELECT d.Name FROM grag.KnowledgeDomain d JOIN grag.DocumentContribution dc ON dc.DomainId=d.DomainId
            WHERE dc.DocumentId=@doc AND dc.ItemKind=3;
            SELECT c.Name FROM grag.KnowledgeCategory c JOIN grag.DocumentContribution dc ON dc.CategoryId=c.CategoryId
            WHERE dc.DocumentId=@doc AND dc.ItemKind=4;
            SELECT COUNT(*) FROM grag.KnowledgeChunk c JOIN grag.SourceDocument d ON d.EntityId=c.EntityId
            WHERE d.DocumentId=@doc AND c.ScopeKey=@scope AND c.Embedding IS NOT NULL;
            """, connection);
        command.Parameters.AddWithValue("@doc", documentId);
        command.Parameters.AddWithValue("@scope", scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        var edges = new List<EdgeSnapshot>();
        while (await reader.ReadAsync(ct))
        {
            var (description, obligation) = PolicyObligation.Read(reader.IsDBNull(3) ? null : reader.GetString(3));
            edges.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), description) { Obligation = obligation });
        }
        await reader.NextResultAsync(ct);
        var domains = new List<string>();
        while (await reader.ReadAsync(ct)) domains.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        var categories = new List<string>();
        while (await reader.ReadAsync(ct)) categories.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        return new(names.ToArray(), edges, domains.ToArray(), categories.ToArray(), reader.GetInt32(0));
    }
}
