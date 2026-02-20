using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fabr.Sdk.Memory;

/// <summary>
/// SQL Server implementation of IMemoryStore using native VECTOR type and VECTOR_DISTANCE().
/// Combines vector similarity search with keyword matching via reciprocal rank fusion.
/// </summary>
public sealed class SqlServerMemoryStore : IMemoryStore
{
    private readonly MemoryOptions _options;
    private readonly ILogger<SqlServerMemoryStore> _logger;
    private bool _initialized;

    public SqlServerMemoryStore(IOptions<MemoryOptions> options, ILogger<SqlServerMemoryStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        var sql = $"""
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MemoryChunks')
            BEGIN
                CREATE TABLE dbo.MemoryChunks (
                    Id INT PRIMARY KEY IDENTITY(1,1),
                    Content NVARCHAR(MAX) NOT NULL,
                    SourcePath NVARCHAR(500) NOT NULL,
                    SourceType NVARCHAR(50) NULL,
                    Title NVARCHAR(500) NULL,
                    ChunkIndex INT NOT NULL DEFAULT 0,
                    IndexedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    Embedding VECTOR({_options.Dimensions}) NOT NULL
                );

                CREATE INDEX IX_MemoryChunks_SourcePath ON dbo.MemoryChunks(SourcePath);
            END
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        _initialized = true;
        _logger.LogInformation("SqlServerMemoryStore initialized (dimensions={Dimensions})", _options.Dimensions);
    }

    public async Task WriteAsync(MemoryChunk chunk)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        var sql = $"""
            INSERT INTO dbo.MemoryChunks (Content, SourcePath, SourceType, Title, ChunkIndex, IndexedAt, Embedding)
            VALUES (@Content, @SourcePath, @SourceType, @Title, @ChunkIndex, @IndexedAt, CAST(@Embedding AS VECTOR({_options.Dimensions})));
            """;

        await using var cmd = new SqlCommand(sql, conn);
        AddChunkParameters(cmd, chunk);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task WriteBatchAsync(IReadOnlyList<MemoryChunk> chunks)
    {
        if (chunks.Count == 0) return;
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();
        await using var txn = conn.BeginTransaction();

        try
        {
            foreach (var chunk in chunks)
            {
                var sql = $"""
                    INSERT INTO dbo.MemoryChunks (Content, SourcePath, SourceType, Title, ChunkIndex, IndexedAt, Embedding)
                    VALUES (@Content, @SourcePath, @SourceType, @Title, @ChunkIndex, @IndexedAt, CAST(@Embedding AS VECTOR({_options.Dimensions})));
                    """;

                await using var cmd = new SqlCommand(sql, conn, txn);
                AddChunkParameters(cmd, chunk);
                await cmd.ExecuteNonQueryAsync();
            }

            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
    }

    public async Task<List<MemorySearchResult>> SearchAsync(float[] queryEmbedding, string queryText, int maxResults = 6)
    {
        var vectorResults = await VectorSearchAsync(queryEmbedding, maxResults * 2);
        var keywordResults = await KeywordSearchAsync(queryText, maxResults * 2);

        // Reciprocal Rank Fusion
        var fusedScores = new Dictionary<string, (MemorySearchResult Result, float Score)>();

        for (int i = 0; i < vectorResults.Count; i++)
        {
            var r = vectorResults[i];
            var key = $"{r.SourcePath}:{r.ChunkIndex}";
            var rrfScore = _options.VectorWeight / (_options.RrfK + i + 1);

            if (fusedScores.TryGetValue(key, out var existing))
                fusedScores[key] = (existing.Result, existing.Score + rrfScore);
            else
                fusedScores[key] = (r, rrfScore);
        }

        for (int i = 0; i < keywordResults.Count; i++)
        {
            var r = keywordResults[i];
            var key = $"{r.SourcePath}:{r.ChunkIndex}";
            var rrfScore = _options.KeywordWeight / (_options.RrfK + i + 1);

            if (fusedScores.TryGetValue(key, out var existing))
                fusedScores[key] = (existing.Result, existing.Score + rrfScore);
            else
                fusedScores[key] = (r, rrfScore);
        }

        return fusedScores.Values
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x =>
            {
                x.Result.Score = x.Score;
                return x.Result;
            })
            .ToList();
    }

    public async Task<List<MemorySearchResult>> VectorSearchAsync(float[] queryEmbedding, int maxResults)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        var sql = $"""
            SELECT TOP (@MaxResults)
                Content, SourcePath, Title, ChunkIndex,
                VECTOR_DISTANCE('cosine', CAST(@Query AS VECTOR({_options.Dimensions})), Embedding) AS Distance
            FROM dbo.MemoryChunks
            ORDER BY Distance;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@MaxResults", maxResults);
        cmd.Parameters.AddWithValue("@Query", JsonSerializer.Serialize(queryEmbedding));

        var results = new List<MemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new MemorySearchResult
            {
                Content = reader.GetString(0),
                SourcePath = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                // Convert cosine distance [0,2] to similarity score [0,1]
                Score = 1f - ((float)reader.GetDouble(4) / 2f)
            });
        }

        return results;
    }

    public async Task DeleteBySourceAsync(string sourcePath)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "DELETE FROM dbo.MemoryChunks WHERE SourcePath = @SourcePath", conn);
        cmd.Parameters.AddWithValue("@SourcePath", sourcePath);

        var deleted = await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("Deleted {Count} chunks for source: {Source}", deleted, sourcePath);
    }

    public async Task<List<MemorySearchResult>> GetBySourceAsync(string sourcePath)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT Content, SourcePath, Title, ChunkIndex FROM dbo.MemoryChunks WHERE SourcePath = @SourcePath ORDER BY ChunkIndex", conn);
        cmd.Parameters.AddWithValue("@SourcePath", sourcePath);

        var results = new List<MemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new MemorySearchResult
            {
                Content = reader.GetString(0),
                SourcePath = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                Score = 1f
            });
        }

        return results;
    }

    public async Task<(int TotalChunks, int UniqueSources)> GetStatsAsync()
    {
        await EnsureInitializedAsync();

        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT COUNT(*), COUNT(DISTINCT SourcePath) FROM dbo.MemoryChunks", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        return (0, 0);
    }

    private async Task<List<MemorySearchResult>> KeywordSearchAsync(string queryText, int maxResults)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync();

        var keywords = queryText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 2)
            .Take(5)
            .ToArray();

        if (keywords.Length == 0)
            return [];

        var conditions = new List<string>();
        for (int i = 0; i < keywords.Length; i++)
        {
            conditions.Add($"CASE WHEN Content LIKE @Kw{i} THEN 1 ELSE 0 END");
        }

        var sql = $"""
            SELECT TOP (@MaxResults)
                Content, SourcePath, Title, ChunkIndex,
                ({string.Join(" + ", conditions)}) AS MatchCount
            FROM dbo.MemoryChunks
            WHERE {string.Join(" OR ", keywords.Select((_, i) => $"Content LIKE @Kw{i}"))}
            ORDER BY MatchCount DESC;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@MaxResults", maxResults);

        for (int i = 0; i < keywords.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@Kw{i}", $"%{keywords[i]}%");
        }

        var results = new List<MemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var matchCount = reader.GetInt32(4);
            results.Add(new MemorySearchResult
            {
                Content = reader.GetString(0),
                SourcePath = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                Score = (float)matchCount / keywords.Length
            });
        }

        return results;
    }

    private static void AddChunkParameters(SqlCommand cmd, MemoryChunk chunk)
    {
        cmd.Parameters.AddWithValue("@Content", chunk.Content);
        cmd.Parameters.AddWithValue("@SourcePath", chunk.SourcePath);
        cmd.Parameters.AddWithValue("@SourceType", (object?)chunk.SourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Title", (object?)chunk.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ChunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("@IndexedAt", chunk.IndexedAt);
        cmd.Parameters.AddWithValue("@Embedding", JsonSerializer.Serialize(chunk.Embedding));
    }
}
