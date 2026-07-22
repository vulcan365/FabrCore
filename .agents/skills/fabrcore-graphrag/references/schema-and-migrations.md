# Schema And Migrations Reference

## Table Of Contents

- Schema name
- Schema initialization
- Migration runner
- Core tables
- Taxonomy tables
- Source and provenance tables
- Audit and metrics tables
- Indexes
- Operational notes

## Schema Name

The schema is fixed as:

```sql
grag
```

Do not rename it in consuming apps. The service SQL assumes this schema name.

## Schema Initialization

Call path:

```csharp
GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logger)
```

`AddGraphRagServices` runs this automatically through a hosted service.

## Migration Runner

The migration runner maintains:

- `grag.SchemaVersion`

Registered migrations currently include:

- `M001_BaselineSchema`
- `M002_ActionAudit`
- `M003_SourceDocumentMetadata`

Migrations are intended to be idempotent and append-only. Add new migrations by
implementing `IGraphRagMigration` and registering them in `Migrations.Registered`.

## Core Tables

`grag.KnowledgeEntity`

- SQL graph node table.
- Stores entities, documents, concepts, people, systems, and other extracted
  knowledge nodes.
- Includes `ScopeKey`.
- Includes `Embedding VECTOR(1536)`.
- Scope is the access boundary.

`grag.KnowledgeRelationship`

- SQL graph edge table.
- Stores typed relationships between knowledge entities.
- Includes relationship type, description, and weight.

`grag.KnowledgeChunk`

- Standard table.
- Stores chunks associated with entities/documents.
- Includes `ScopeKey`.
- Includes `Embedding VECTOR(1536)`.

## Taxonomy Tables

`grag.KnowledgeDomain`

- SQL graph node table.
- Represents a high-level taxonomy domain.

`grag.KnowledgeCategory`

- SQL graph node table.
- Represents a category, usually under a domain.

`grag.BelongsTo`

- SQL graph edge table.
- Links entities/categories/domains depending on taxonomy shape.

`grag.CommunitySummary`

- Stores generated or curated community/category summaries.

Domains and categories are classification/provenance. They are not security
boundaries.

## Source And Provenance Tables

`grag.SourceDocument`

- Tracks source document identity and ingest state.
- Important columns include `DocumentId`, `FileName`, `ScopeKey`, `SourceKind`,
  `SourceKey`, `SourceTitle`, `SourceOccurredAtUtc`, `MetadataJson`,
  `ContentHash`, `VersionNumber`, status fields, and count fields.
- M003 adds/backfills first-class source metadata.

`grag.DocumentContribution`

- Tracks the rows a document contributed to the graph.
- Used for diagnostics, re-ingest, deletion cleanup, and orphan analysis.

## Audit And Metrics Tables

`grag.ActionAudit`

- Added by M002.
- Records user/admin/system actions.
- Indexed by time, action, actor, scope, and subject.

`grag.IngestionMetric`

- Records ingestion token/call/duration metrics.
- Indexed by document, creation time, and scope.

## Important Indexes

Examples:

- `IX_KnowledgeEntity_Name_Type_Scope`
- `IX_KnowledgeEntity_ScopeKey`
- `IX_KnowledgeChunk_EntityId_Index`
- `IX_KnowledgeChunk_Scope_Entity`
- `UX_SourceDocument_Scope_Source`
- `IX_SourceDocument_Scope_FileName`
- `IX_ActionAudit_Time`
- `IX_ActionAudit_Action`
- `IX_ActionAudit_Actor`
- `IX_ActionAudit_Scope`
- `IX_IM_Document`
- `IX_IM_CreatedAt`
- `IX_IM_Scope`

## Operational Notes

- The database must support SQL graph tables.
- The database must support `VECTOR(1536)`.
- Schema initialization should run with permissions to create schema, tables,
  indexes, constraints, and migration rows.
- Runtime service accounts need read/write access to `grag` tables.
- Production deployments should capture schema initialization logs.
- Failed startup schema initialization usually means a missing connection string,
  insufficient SQL permissions, or unsupported SQL Server vector features.

