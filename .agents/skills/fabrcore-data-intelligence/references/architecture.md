# FabrCore.Services.DataIntelligence — Architecture

## Execution Pipeline

```
                          Agent / Plugin
                               |
                    +----------v----------+
                    | DataIntelligenceCtx  |  FormatForSystemPrompt()
                    | (schema + specs)     |  FormatForMessage()
                    +----------+----------+
                               |
                    +----------v----------+
                    | ProjectedQueryExec   |  GetSchema / Preview / Execute
                    +----------+----------+
                               |
              +----------------+----------------+
              |                |                |
     +--------v-----+  +------v-------+  +-----v--------+
     | Specification |  | Projection   |  | Result       |
     | Evaluator     |  | Builder      |  | Serializer   |
     | (WHERE)       |  | (SELECT)     |  | (JSON+Budget)|
     +--------+------+  +------+-------+  +-----+--------+
              |                |                |
              +-------+--------+                |
                      |                         |
             +--------v---------+               |
             | EF Core IQueryable|              |
             | .Where().Select() |              |
             +--------+---------+               |
                      |                         |
             +--------v---------+               |
             | SQL Server        |              |
             | SELECT [cols]     |              |
             | FROM [table]      |              |
             | WHERE [criteria]  |              |
             +--------+---------+               |
                      |                         |
             +--------v---------+       +-------v--------+
             | List<object?[]>   |----->| Dict<str,obj?> |
             | (materialized)    |      | (cleaned)      |
             +-------------------+      +-------+--------+
                                                |
                                        +-------v--------+
                                        | QueryResult    |
                                        | .SerializedJson|
                                        | (ready for LLM)|
                                        +----------------+
```

## Progressive Disclosure Flow

```
Agent OnInitialize
  |
  +-> DataIntelligenceContext.FormatForSystemPrompt()
  |   Returns: entity names, column names + types, filter signatures
  |   Token cost: ~1-2K per entity
  |   DB queries: 0
  |
  v
LLM receives system prompt with schema awareness
  |
Agent OnMessage (user asks a data question)
  |
  +-> Step 1: GetSchema<T>()      [if LLM needs column details]
  |   Returns: all column names, types, nullability
  |   Token cost: ~2K
  |   DB queries: 0
  |
  +-> Step 2: PreviewAsync<T>()   [small sample + count]
  |   Returns: 3 rows + totalMatchingRows
  |   Token cost: ~500
  |   DB queries: 2 (COUNT + SELECT TOP 3)
  |
  +-> Step 3: ExecuteAsync<T>()   [full budget-aware query]
  |   Returns: projected rows within character budget
  |   Token cost: hard-capped by CharacterBudget
  |   DB queries: 1-2 (optional COUNT + SELECT)
  |
  v
result.SerializedJson -> return to LLM
```

## Specification Pattern Architecture

```
              JSON Request (SpecificationGroup[])
                         |
              +----------v-----------+
              | SpecificationFactory  |
              | CreateFromGroups()    |
              +----------+-----------+
                         |
              +----------v-----------+
              | ISpecification<T>     |
              | .Criteria             |  Expression<Func<T, bool>>
              | .Includes             |  Navigation includes
              +----------+-----------+
                         |
              +----------v-----------+
              | SpecificationEvaluator|
              | GetQuery()            |
              +----------+-----------+
                         |
              +----------v-----------+
              | IQueryable<T>         |
              | .Where(criteria)      |
              | .Include(nav)         |
              +----------+-----------+
                         |
                    SQL Execution
```

## Component Responsibilities

| Component | Responsibility | Touches DB |
|-----------|---------------|------------|
| `EntitySchemaService` | Reflect entity types, cache schema metadata | No |
| `SpecificationFactory<T>` | Discover specs, create from JSON params | No |
| `SpecificationMetadataService` | Aggregate metadata from multiple factories | No |
| `DataIntelligenceContext` | Unified schema + specs, format for LLM | No |
| `SpecificationEvaluator<T>` | Apply WHERE + INCLUDE to IQueryable | No (builds query) |
| `ProjectionBuilder` | Build dynamic Select expression | No (builds query) |
| `ProjectedQueryExecutor` | Orchestrate: filter → count → page → project → serialize | Yes |
| `QueryExecutor` | Simple filter + materialize (no projection) | Yes |
| `CascadeFilter` | In-memory child/grandchild filtering | No (post-query) |
| `ResultSerializer` | Budget-aware JSON with null/default cleaning | No |

## Database Query Patterns

### Schema Mode
```
Zero queries — pure reflection
```

### Preview Mode
```sql
-- Query 1: COUNT
SELECT COUNT(*) FROM [Orders] WHERE [Status] = @p0

-- Query 2: Sample
SELECT TOP(3) [o].[Id], [o].[OrderNumber], [o].[Status]
FROM [Orders] AS [o]
WHERE [o].[Status] = @p0
```

### Full Mode
```sql
-- Query 1: COUNT (optional)
SELECT COUNT(*) FROM [Orders] WHERE [Status] = @p0

-- Query 2: Projected data (OrderBy = "OrderNumber" in QueryOptions)
SELECT [o].[Id], [o].[OrderNumber], [o].[Status], [o].[Total]
FROM [Orders] AS [o]
WHERE [o].[Status] = @p0
ORDER BY [o].[OrderNumber]
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
```

Without `QueryOptions.OrderBy`, EF Core emits `ORDER BY (SELECT 1)` — no ordering
guarantee, so paginated pages can repeat or miss rows. Always set `OrderBy` when paging.

All executors prepend `AsNoTracking()` unless `TrackChanges`/`trackChanges` is set —
results are reporting data, never change-tracked by default.

## Token Budget Architecture

```
Query Result Envelope
+-----------------------------------------------+
| metadata (always included, ~200 chars)         |
|   entityType, columns, totalMatchingRows,      |
|   rowsReturned, wasTruncated, mode             |
+-----------------------------------------------+
| data (budget-controlled)                       |
|   Row 1: { col1: val, col2: val } ~100 chars  |  ← always included
|   Row 2: { col1: val, col2: val } ~100 chars  |  ← included if budget allows
|   Row 3: { col1: val, col2: val } ~100 chars  |  ← included if budget allows
|   ...                                          |
|   Row N: TRUNCATED (budget hit)                |
+-----------------------------------------------+

Budget enforcement:
1. First row always included (even if exceeds budget)
2. Each subsequent row's JSON is measured before inclusion
3. When cumulative chars would exceed CharacterBudget → stop
4. WasTruncated = true signals LLM to refine query
```

## Value Cleaning Pipeline

```
Raw Row from EF Core:
{ "Id": "abc-123", "Name": "Widget", "Description": null, "Quantity": 0,
  "IsActive": false, "CategoryId": "00000000-0000-0000-0000-000000000000",
  "CreatedAt": "0001-01-01T00:00:00" }

After OmitNullValues:
{ "Id": "abc-123", "Name": "Widget", "Quantity": 0,
  "IsActive": false, "CategoryId": "00000000-0000-0000-0000-000000000000",
  "CreatedAt": "0001-01-01T00:00:00" }

After OmitDefaultValues:
{ "Id": "abc-123", "Name": "Widget" }

Token savings: 7 properties → 2 properties (71% reduction per row)
```
