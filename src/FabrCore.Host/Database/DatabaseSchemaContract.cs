using Microsoft.Data.SqlClient;

namespace FabrCore.Host.Database;

/// <summary>Required 2.0 storage shape. Update alongside schema migrations and store queries.</summary>
internal static class DatabaseSchemaContract
{
    internal sealed record Table(string Name, string Kind, string Columns);
    internal static readonly Table[] Tables =
    [
        new("grag.SchemaVersion", "table", "Version:bigint,Description:nvarchar,AppliedAt:datetime2,DurationMs:int,AppliedBy:nvarchar"),
        new("mem.MemoryExtractionReceipt", "table", "ScopeKey:nvarchar,SourceHash:char,EntityIds:nvarchar,CreatedAt:datetime2"),
        new("mem.MemoryEntity", "node", "EntityId:uniqueidentifier,ScopeKey:nvarchar,Name:nvarchar,EntityType:nvarchar,Description:nvarchar,Content:nvarchar,Visibility:nvarchar,IsPointInTime:bit,Metadata:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("mem.MemoryRelationship", "edge", "ScopeKey:nvarchar,RelationshipType:nvarchar,Description:nvarchar,Weight:float,Metadata:nvarchar,CreatedAt:datetime2"),
        new("mem.MemoryChunk", "table", "ChunkId:uniqueidentifier,ScopeKey:nvarchar,EntityId:uniqueidentifier,Content:nvarchar,Embedding:vector,ChunkIndex:int,Metadata:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("mem.MemorySummaryNode", "table", "NodeId:uniqueidentifier,ScopeKey:nvarchar,ParentNodeId:uniqueidentifier,Depth:int,Topic:nvarchar,Summary:nvarchar,Embedding:vector,MemberCount:int,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("mem.MemoryScope", "table", "ScopeKey:nvarchar,Description:nvarchar,IsShared:bit,CreatedAt:datetime2,CreatedBy:nvarchar"),
        new("mem.MemoryAuditLog", "table", "AuditId:bigint,OccurredAt:datetime2,ActionType:nvarchar,ScopeKey:nvarchar,MemoryId:uniqueidentifier,ActorId:nvarchar,ActorName:nvarchar,Summary:nvarchar,Payload:nvarchar,DurationMs:bigint"),
        new("grag.KnowledgeEntity", "node", "EntityId:uniqueidentifier,CanonicalEntityId:uniqueidentifier,Name:nvarchar,EntityType:nvarchar,ScopeKey:nvarchar,Description:nvarchar,Content:nvarchar,Embedding:vector,Metadata:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("grag.CanonicalEntity", "table", "CanonicalEntityId:uniqueidentifier,Name:nvarchar,EntityType:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("grag.KnowledgeRelationship", "edge", "ScopeKey:nvarchar,RelationshipType:nvarchar,Description:nvarchar,Weight:float,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.KnowledgeChunk", "table", "ChunkId:uniqueidentifier,EntityId:uniqueidentifier,ScopeKey:nvarchar,Content:nvarchar,Embedding:vector,ChunkIndex:int,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.KnowledgeScope", "table", "ScopeKey:nvarchar,Description:nvarchar,DefaultPriority:float,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.KnowledgeDomain", "node", "DomainId:uniqueidentifier,Name:nvarchar,Description:nvarchar,PriorityWeight:float,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.KnowledgeCategory", "node", "CategoryId:uniqueidentifier,Name:nvarchar,Description:nvarchar,Embedding:vector,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.BelongsTo", "edge", "ScopeKey:nvarchar,Metadata:nvarchar,CreatedAt:datetime2"),
        new("grag.CommunitySummary", "table", "SummaryId:uniqueidentifier,CategoryId:uniqueidentifier,ScopeKey:nvarchar,Summary:nvarchar,Embedding:vector,EntityCount:int,Metadata:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("grag.SourceDocument", "table", "DocumentId:uniqueidentifier,FileName:nvarchar,ScopeKey:nvarchar,SourceKind:nvarchar,SourceKey:nvarchar,SourceTitle:nvarchar,SourceOccurredAtUtc:datetime2,MetadataJson:nvarchar,MarkdownContent:nvarchar,FileSizeBytes:bigint,EntityId:uniqueidentifier,ChunkCount:int,ExtractedEntityCount:int,ExtractedRelationshipCount:int,ContentHash:char,InstructionHash:char,VersionNumber:int,LockedAt:datetime2,LockedBy:nvarchar,Status:nvarchar,ErrorMessage:nvarchar,CreatedAt:datetime2,UpdatedAt:datetime2"),
        new("grag.DocumentContribution", "table", "ContributionId:uniqueidentifier,DocumentId:uniqueidentifier,ItemKind:tinyint,EntityId:uniqueidentifier,RelFromEntityId:uniqueidentifier,RelToEntityId:uniqueidentifier,RelationshipType:nvarchar,DomainId:uniqueidentifier,CategoryId:uniqueidentifier,BelongsToShape:tinyint,VersionNumber:int,CreatedAt:datetime2"),
        new("grag.IngestionMetric", "table", "MetricId:uniqueidentifier,DocumentId:uniqueidentifier,VersionNumber:int,ScopeKey:nvarchar,ChatModelName:nvarchar,ChatInputTokens:bigint,ChatOutputTokens:bigint,ChatCallCount:int,ChatTotalMs:bigint,DurationMs:bigint,CreatedAt:datetime2,ResolvedModelName:nvarchar,ChunkEmbeddingMs:bigint,DocumentEmbeddingMs:bigint,LlmExtractionMs:bigint,EntityEmbeddingMs:bigint,SqlWriteMs:bigint,EmbeddingBatchCount:int,SqlCommandBatchCount:int,ChunkCount:int,ExtractedEntityCount:int,ExtractedRelationshipCount:int,ResolvedProviderName:nvarchar,ResolvedDeploymentModelName:nvarchar,ExtractionBatchCount:int,ExtractionRetryCount:int,ExtractionTruncationCount:int"),
        new("grag.ActionAudit", "table", "AuditId:bigint,OccurredAt:datetime2,ActionType:nvarchar,Severity:tinyint,ActorKind:nvarchar,ActorId:nvarchar,ActorName:nvarchar,SubjectKind:nvarchar,SubjectId:nvarchar,ScopeKey:nvarchar,CorrelationId:uniqueidentifier,DurationMs:bigint,Summary:nvarchar,Payload:nvarchar"),
        new("acl.Configuration", "table", "Id:int,Version:bigint,ModeOverride:int"),
        new("acl.Principal", "table", "Handle:nvarchar,DisplayName:nvarchar,IsSystem:bit,Description:nvarchar"),
        new("acl.Role", "table", "Name:nvarchar,Description:nvarchar,IsBuiltIn:bit"),
        new("acl.Group", "table", "Name:nvarchar,Description:nvarchar,IsDynamic:bit"),
        new("acl.PrincipalRole", "table", "Handle:nvarchar,RoleName:nvarchar"),
        new("acl.GroupRole", "table", "Name:nvarchar,RoleName:nvarchar"),
        new("acl.GroupMember", "table", "Name:nvarchar,Kind:int,Handle:nvarchar"),
        new("acl.PermissionGrant", "table", "OwnerRole:nvarchar,Id:nvarchar,SubjectKind:int,Selector:nvarchar,Permission:nvarchar,Resource:nvarchar"),
        new("fabrOps.SchemaVersion", "table", "Version:int"),
        new("fabrOps.Audit", "table", "Id:nvarchar,Timestamp:datetimeoffset,Category:int,Outcome:int,SubjectPrincipal:nvarchar,ResourcePrincipal:nvarchar,TraceId:nvarchar,Payload:nvarchar"),
        new("fabrOps.EvidenceSequence", "table", "TraceId:nvarchar,SegmentId:nvarchar,NextSequence:bigint"),
        new("fabrOps.Evidence", "table", "TraceId:nvarchar,SegmentId:nvarchar,Sequence:bigint,RecordId:nvarchar,Timestamp:datetimeoffset,RecordJson:nvarchar,SignatureJson:nvarchar,CertificateJson:nvarchar"),
        new("fabrOps.Attestation", "table", "TraceId:nvarchar,Id:nvarchar,Payload:nvarchar"),
        new("fabrOps.A2ATask", "table", "Id:nvarchar,OwnerFingerprint:nvarchar,InstanceId:uniqueidentifier,LeaseUntil:datetime2,Terminal:bit,CancelRequested:bit,SavedAt:datetime2,Payload:nvarchar"),
    ];

    internal static async Task ValidateAsync(string connectionString, string schema, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        if (schema == "mem")
        {
            await using var index = new SqlCommand("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('mem.MemoryEntity') AND name='IX_MemoryEntity_Scope_Name_Type' AND is_disabled=0", connection);
            if (Convert.ToInt32(await index.ExecuteScalarAsync(ct)) != 1)
                throw Incompatible("mem.MemoryEntity.IX_MemoryEntity_Scope_Name_Type", "required memory index is missing or disabled");
        }
        foreach (var table in Tables.Where(t => t.Name.StartsWith(schema + ".", StringComparison.Ordinal)))
        {
            await using var command = new SqlCommand("""
                SELECT c.name, CASE WHEN c.vector_dimensions IS NOT NULL THEN 'vector' ELSE TYPE_NAME(c.system_type_id) END, c.vector_dimensions, t.is_node, t.is_edge
                FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
                WHERE t.object_id=OBJECT_ID(@table, 'U');
                """, connection);
            command.Parameters.AddWithValue("@table", table.Name);
            var columns = new Dictionary<string, (string Type, int? Dimensions)>(StringComparer.OrdinalIgnoreCase);
            string? kind = null;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
                    kind = reader.GetBoolean(3) ? "node" : reader.GetBoolean(4) ? "edge" : "table";
                }
            }
            if (kind is null) throw Incompatible(table.Name, "required table is missing");
            if (kind != table.Kind) throw Incompatible(table.Name, $"expected {table.Kind}, found {kind}");
            foreach (var specification in table.Columns.Split(','))
            {
                var parts = specification.Split(':');
                var name = parts[0];
                if (!columns.TryGetValue(name, out var column)) throw Incompatible(table.Name + "." + name, "required column is missing");
                if (!string.Equals(column.Type, parts[1], StringComparison.OrdinalIgnoreCase))
                    throw Incompatible(table.Name + "." + name, $"expected {parts[1]}, found {column.Type}");
                if (parts[1] == "vector" && column.Dimensions != 1536)
                    throw Incompatible(table.Name + "." + name, $"expected VECTOR(1536), found dimensions {column.Dimensions}");
            }
        }
    }

    private static InvalidOperationException Incompatible(string name, string reason) => new(
        $"FabrCore SQL schema '{name}' is incompatible: {reason}. Apply the supported schema migrations or provision a compatible database before restarting. Existing data has not been rebuilt; vector changes require an explicit data migration.");
}
