# FabrCore.Services.DataIntelligence — Pitfalls and Common Mistakes

## 1. Returning All Columns to LLM

**Problem:** Using `QueryExecutor.ExecuteAsync()` and serializing the full `List<T>` sends all 600 columns per row to the LLM, consuming 200K+ tokens for a simple query.

**Fix:** Use `ProjectedQueryExecutor.ExecuteAsync()` with `ColumnProjection.For(...)` to select only needed columns. The projection happens at the database level (SQL SELECT), not in-memory.

```csharp
// BAD: returns all 600 columns
var orders = await QueryExecutor.ExecuteAsync(query, specs, factory, ct);
var json = JsonSerializer.Serialize(orders); // 200K+ tokens

// GOOD: returns only 5 columns
var result = await ProjectedQueryExecutor.ExecuteAsync(query, specs, factory, new QueryOptions
{
    Projection = ColumnProjection.For("Id", "OrderNumber", "Status", "Total", "OrderDate"),
    CharacterBudget = 30000
});
return result.SerializedJson; // ~3K tokens
```

## 2. No Character Budget

**Problem:** Even with column projection, 50,000 rows × 5 columns is still enormous. Without a character budget, the serializer produces unlimited output.

**Fix:** Always set `CharacterBudget` when results are going to an LLM. 30,000 characters (~7,500 tokens) is a good default.

```csharp
new QueryOptions
{
    Projection = ColumnProjection.For("Id", "Name"),
    MaxRows = 100,
    CharacterBudget = 30000  // Hard cap
}
```

## 3. Skipping Schema Discovery

**Problem:** The LLM guesses column names, gets them wrong, and the query fails or returns useless data.

**Fix:** Use `ProjectedQueryExecutor.GetSchema<T>()` first. The LLM sees all available columns and their types, then picks what it needs.

```csharp
// Step 1: Schema (0 DB queries)
var schema = ProjectedQueryExecutor.GetSchema<Order>();
// Step 2: LLM picks columns from schema
// Step 3: Projected query with selected columns
```

## 4. Skipping Preview Before Full Query

**Problem:** The LLM requests 50 rows of data without knowing the result set has 50,000 matches. Wastes tokens on data it may not need.

**Fix:** Use `PreviewAsync` to show the LLM 3 sample rows + total count. It can then decide whether to add more filters or request more data.

```csharp
var preview = await ProjectedQueryExecutor.PreviewAsync(
    query, specs, factory,
    ColumnProjection.For("Id", "OrderNumber", "Status"),
    sampleSize: 3);
// preview.Metadata.TotalMatchingRows = 47,231
// LLM decides: "I need more filters to narrow this down"
```

## 5. COUNT on Large Unfiltered Tables

**Problem:** `IncludeTotalCount = true` (the default) runs `SELECT COUNT(*) FROM [Table]` which can be slow on million-row tables with no WHERE clause.

**Fix:** Set `IncludeTotalCount = false` when you don't need the total count, or ensure specifications are applied to narrow the result set first.

```csharp
new QueryOptions
{
    IncludeTotalCount = false,  // Skip expensive COUNT
    MaxRows = 20
}
```

## 6. Projecting Navigation Properties

**Problem:** Requesting a navigation property like `"Lines"` or `"Customer"` in the column list causes a confusing error or loads entire object graphs.

**Fix:** `ProjectionBuilder` throws a clear error: "Column 'X' is a navigation property — use the foreign key property instead (e.g., 'CustomerId')." Use FK properties for relationships.

```csharp
// BAD: navigation property
ColumnProjection.For("Id", "Customer")  // Throws ArgumentException

// GOOD: foreign key
ColumnProjection.For("Id", "CustomerId")
```

## 7. Case-Sensitive Column Names

**Problem:** The LLM sends `"ordernumber"` but the property is `"OrderNumber"`.

**Fix:** Column resolution is case-insensitive. `ProjectionBuilder.ResolveColumns<T>()` matches case-insensitively and returns the actual casing. No action needed — this works automatically.

## 8. Missing [SpecificationFor<T>] Attribute

**Problem:** Specification class exists but factory throws `Unknown specification: X`.

**Fix:** Add `[SpecificationFor<T>]` to the class. The factory scans for this attribute — without it, the class is invisible.

```csharp
[SpecificationFor<Order>]  // Required!
[SpecificationDescription("Filters by status")]
public class OrderByStatusSpecification : ByEnumSpecification<Order, OrderStatus> { ... }
```

## 9. Parameter Name Mismatch

**Problem:** Factory throws `Required parameter 'X' not provided` even though you're passing the value.

**Fix:** The JSON parameter name must match the constructor parameter name (case-insensitive). Check constructor args, not property names.

```csharp
// Constructor: public OrderByStatusSpecification(OrderStatus status)
// JSON must use "status" (not "Status" or "orderStatus")
{ "name": "OrderByStatus", "status": "Pending" }
```

## 10. Forgetting to Register Entity Types in EntitySchemaService

**Problem:** `schemaService.GetSchema("Order")` returns null even though `GetSchema<Order>()` works.

**Fix:** String-based lookup requires the type to be registered in the constructor. Either register all types upfront, or use the generic method which auto-registers.

```csharp
// Register all entity types
var schemaService = new EntitySchemaService(
    typeof(Order), typeof(OrderLine), typeof(Product));
```

## 11. Not Registering DataIntelligenceContext in DI

**Problem:** `serviceProvider.GetRequiredService<DataIntelligenceContext>()` throws because the context isn't registered.

**Fix:** Register all three dependencies:

```csharp
services.AddSingleton<IEntitySchemaService>(new EntitySchemaService(...));
services.AddSingleton<ISpecificationMetadataService>(sp => SpecificationMetadataService.Create(...));
services.AddSingleton<DataIntelligenceContext>();
```

## 12. Circular Reference Serialization with QueryExecutor

**Problem:** Using `QueryExecutor` (returns `List<T>`) with JSON serialization hits circular references when entities have navigation properties.

**Fix:** Use `ProjectedQueryExecutor` instead — it projects to `Dictionary<string, object?>` which has no circular references. Or return DTOs from `QueryExecutor` results.

## 13. Child/Grandchild Filtering Performance

**Problem:** `CascadeFilter` is slow because child and grandchild filtering happens in-memory after all parents are materialized.

**Fix:** This is by design — EF Core can't translate child-level specification expressions into SQL subqueries. Only `RootSpecifications` run as SQL WHERE clauses. Keep parent result sets small with root-level filters, and child/grandchild filtering will be fast.

## 14. OmitDefaultValues Hiding Intentional Zeros

**Problem:** `OmitDefaultValues = true` removes `0` from numeric fields, but some zeros are meaningful (e.g., balance = 0, count = 0).

**Fix:** Set `OmitDefaultValues = false` when zeros carry meaning. Keep `OmitNullValues = true` (nulls are almost never meaningful in JSON output).

```csharp
new QueryOptions
{
    OmitNullValues = true,
    OmitDefaultValues = false  // Keep intentional zeros
}
```

## 15. Using Flat vs Nested JSON Parameters

**Problem:** API receives `{ "name": "OrderByStatus", "parameters": { "status": "Pending" } }` but the spec expects flat: `{ "name": "OrderByStatus", "status": "Pending" }`.

**Fix:** Both work. `SpecificationParameter.Parameters` merges `ExplicitParameters` (nested) and `ExtensionParameters` (flat via `[JsonExtensionData]`). Explicit takes precedence on conflicts.

## 16. WasTruncated but No TotalMatchingRows

**Problem:** `WasTruncated = true` but `TotalMatchingRows` is null, so the LLM doesn't know how many rows exist.

**Fix:** Keep `IncludeTotalCount = true` (the default). The COUNT query is separate and efficient with filters applied.

## 17. EntitySchemaService Cache is Static

**Problem:** Different instances of `EntitySchemaService` share the same type cache. Schema info computed once is reused everywhere.

**Fix:** This is intentional — entity schemas don't change at runtime. The cache is `ConcurrentDictionary<Type, EntitySchemaInfo>` and is thread-safe. If you somehow need different schema views of the same type (you shouldn't), this won't work.

## 18. EF Core Select Translation with object[]

**Problem:** Concern that `Expression.NewArrayInit(typeof(object), ...)` won't translate to SQL.

**Fix:** This is a well-established EF Core translation pattern used by many projection libraries. EF Core translates `x => new object[] { (object)x.Col1, (object)x.Col2 }` to `SELECT [x].[Col1], [x].[Col2]`. Verified with SQL Server. If you hit a provider that doesn't support this, fall back to `QueryExecutor` with manual DTO mapping.

## 19. SME consultation: do not enumerate other agents' missing tools

**Problem:** When a data-SME agent (e.g. one built from `data-query-agent-template.cs`) is listed in a TaskAgent or Swarm `SubjectMatterExperts` collection, the framework sends a pre-planning consultation that includes the question:

> "Are any tools / capabilities likely to be MISSING for this goal? Be specific — if you know an action is not supported, say so explicitly."

A read-only data SME's LLM reads that question literally and enumerates **its own** limitations ("I don't have write/mutation capability") as if they were swarm-wide capability gaps. The orchestrator's planner ingests that text under a heading labeled "capability gaps," and downstream worker agents conclude no mutation tool exists for the swarm and bail out of perfectly executable tasks. Confidence stays high (~0.98), no error fires — the user just sees "this can't be done" repeatedly with polite warnings like *"No mutation tool exists to detach plate assignments."*

The mutation tools do exist; they live on the **worker agents'** plugins. The data SME has zero visibility into those tool inventories and should not be making claims about them.

**Fix:** Append `DataSmeSystemPromptGuard.Text` to the agent's `SystemPrompt` in `OnInitialize`. The guard explicitly scopes the SME's answer for the "missing tools" question to a canonical "Unknown — I do not have visibility into the worker agents' tool inventory." reply, and forbids speculation about other agents' capabilities.

```csharp
public override async Task OnInitialize()
{
    var diContext = serviceProvider.GetRequiredService<DataIntelligenceContext>();
    config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt();
    config.SystemPrompt += "\n\n" + DataSmeSystemPromptGuard.Text;  // ← add this line
    // ...rest of OnInitialize
}
```

The `data-query-agent-template.cs` template already includes this. Hand-rolled data SMEs (anything that extends `FabrCoreAgentProxy` and may be consulted as an SME) need the one-line addition.

## 20. Paginating Without OrderBy

**Problem:** `Skip`/`MaxRows` used without `OrderBy`. SQL gives no ordering guarantee without an ORDER BY, so consecutive pages can repeat rows or miss them entirely — the LLM silently reasons over incomplete data.

**Fix:** Always set `QueryOptions.OrderBy` (typically the primary key or a date column) whenever `Skip` or `MaxRows` is used:

```csharp
new QueryOptions
{
    OrderBy = "Id",       // validated case-insensitively against the schema
    Skip = 100,
    MaxRows = 50
}
```

Unknown column names throw `ArgumentException` listing the valid columns, same as projection.

## 21. Schema Reflects the CLR Type, Not the EF Model

**Problem:** A `[NotMapped]` property shows up in the schema and the LLM (or a client) tries to project or sort by it — the query fails at EF translation time. Conversely, EF shadow properties never appear.

**Fix:** `EntitySchemaService` reflects over the entity class itself, not EF Core's `IModel`. Keep reportable entities free of unmapped public properties (move computed values to DTOs), and don't rely on shadow properties for reporting. Navigation detection is also type-based: any non-simple class property (or collection of one) is classified as a navigation, whether or not EF maps it as a relationship.

## 22. Duplicate Specification Names

**Problem:** Two specification classes for the same entity resolve to the same name (e.g. `OrderByStatusSpecification` in two scanned assemblies, or two classes with the same `Name` override). The factory throws `InvalidOperationException` at construction — at startup, not at query time.

**Fix:** This is deliberate — a silent overwrite would leave the metadata advertising a filter that `Create()` can never reach. Disambiguate with the attribute:

```csharp
[SpecificationFor<Order>(Name = "OrderByStatusLegacy")]
public class LegacyOrderByStatusSpecification : ... { }
```

The exception message names both conflicting types. Same class discovered twice (overlapping assembly scans) is fine and deduplicated silently.

## 23. Enabling TrackChanges with Cascade Filtering

**Problem:** All executors run `AsNoTracking` by default. If you pass `trackChanges: true` (or `TrackChanges = true`) on a query that also uses `CascadeFilter` or `HierarchicalQueryExecutor` child filters, the in-memory removal of non-matching children mutates *tracked* entities — a later `SaveChanges()` on that DbContext interprets the removals as relationship deletes and can null FKs or delete rows.

**Fix:** Leave tracking off for all reporting queries (the default). Only enable it for flat queries whose results you intend to modify and save — never combined with cascade filtering.
