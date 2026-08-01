using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Services;

internal static class ScopeRegistryStore
{
    public static async Task EnsureExistsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string scopeKey,
        CancellationToken ct = default)
    {
        var sql = $"SELECT COUNT(*) FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeScope WHERE ScopeKey = @scopeKey";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);
        if ((int)(await command.ExecuteScalarAsync(ct))! == 0)
        {
            throw new InvalidOperationException(
                $"Knowledge scope '{scopeKey}' is not registered. Create it with IKnowledgeScopeService before writing knowledge.");
        }
    }
}
