# FabrCore.Services.DataIntelligence — API Reference

## Schema Introspection

### EntityPropertyInfo

```csharp
namespace FabrCore.Services.DataIntelligence.Metadata;

public record EntityPropertyInfo(
    string Name,          // Property name: "OrderNumber"
    string Type,          // Friendly type: "string", "int", "DateTime?", "Guid"
    bool IsNullable,      // True if property accepts null
    bool IsNavigation,    // True if navigation/collection property
    IReadOnlyList<string>? AllowedValues = null); // Enum member names, null for non-enums
```

### EntitySchemaInfo

```csharp
public record EntitySchemaInfo(
    string EntityType,                             // "Order"
    int PropertyCount,                             // Total properties including navigations
    IReadOnlyList<EntityPropertyInfo> Properties,  // All properties with metadata
    IReadOnlyList<string> ScalarPropertyNames,     // Non-navigation property names
    IReadOnlyList<string> NavigationPropertyNames); // Navigation property names
```

### IEntitySchemaService

```csharp
public interface IEntitySchemaService
{
    EntitySchemaInfo GetSchema<T>() where T : class;
    EntitySchemaInfo? GetSchema(string entityTypeName);
    IReadOnlyList<string> GetEntityTypes();
}
```

### EntitySchemaService

```csharp
// Constructor variants
public EntitySchemaService(params Type[] entityTypes);
public EntitySchemaService(IEnumerable<Type> entityTypes);

// Internal (used by ProjectionBuilder)
internal static EntitySchemaInfo GetSchemaForType<T>() where T : class;
```

**Behavior:**
- Reflects the **CLR type**, not EF Core's `IModel` — `[NotMapped]` properties appear as columns, shadow properties don't appear (see pitfalls.md #21)
- Reflects public instance properties via `BindingFlags.Public | BindingFlags.Instance`
- Uses `NullabilityInfoContext` for reference type nullability detection
- Classifies navigations by type: any non-simple class property, or a collection whose element is one (simple = primitives, enums, string, decimal, DateTime/Offset, DateOnly/TimeOnly, TimeSpan, Guid, byte[], and their nullables)
- Enum properties (including nullable enums) populate `EntityPropertyInfo.AllowedValues` with the enum member names
- Caches in static `ConcurrentDictionary<Type, EntitySchemaInfo>`; the name registry is also concurrent — safe as a singleton under parallel agent traffic
- `GetSchema<T>()` auto-registers the type for string-based lookup

## Column Projection

### ColumnProjection

```csharp
namespace FabrCore.Services.DataIntelligence.Projection;

public class ColumnProjection
{
    public IReadOnlyList<string>? Columns { get; init; }     // Explicit columns, null = all
    public bool ExcludeNavigations { get; init; } = true;    // Skip nav props in "all" mode
    public bool HasExplicitColumns { get; }                  // True if Columns has items

    public static ColumnProjection For(params string[] columns);
    public static ColumnProjection AllScalars();
}
```

### ProjectionBuilder

```csharp
namespace FabrCore.Services.DataIntelligence.Projection;

public static class ProjectionBuilder
{
    // Validates column names (case-insensitive), returns resolved names with correct casing
    // Throws ArgumentException for unknown columns (error includes valid column list)
    // Throws ArgumentException for navigation properties (suggests FK instead)
    public static IReadOnlyList<string> ResolveColumns<T>(
        ColumnProjection? projection) where T : class;

    // Builds EF Core-translatable Select expression: x => new object?[] { x.Col1, x.Col2 }
    // EF Core generates: SELECT [x].[Col1], [x].[Col2] FROM [Table] AS [x]
    public static Expression<Func<T, object?[]>> BuildProjection<T>(
        IReadOnlyList<string> columns) where T : class;

    // Zips object?[] row back to named dictionary
    public static Dictionary<string, object?> ToDictionary(
        object?[] row, IReadOnlyList<string> columns);
}
```

## Query Execution

### QueryOptions

```csharp
namespace FabrCore.Services.DataIntelligence.Query;

public class QueryOptions
{
    public ColumnProjection? Projection { get; init; }   // null = all scalar columns
    public string? OrderBy { get; init; }                 // Column to sort by (validated, case-insensitive)
    public bool OrderByDescending { get; init; }          // Sort direction for OrderBy
    public int? MaxRows { get; init; }                    // null = no limit
    public int? Skip { get; init; }                       // null = start at row 0
    public bool IncludeTotalCount { get; init; } = true;  // Execute COUNT query
    public int? CharacterBudget { get; init; }            // null = no character limit
    public bool OmitNullValues { get; init; } = true;     // Remove nulls from JSON
    public bool OmitDefaultValues { get; init; } = true;  // Remove 0, false, empty Guid, etc.
    public bool TrackChanges { get; init; }               // false (default) = AsNoTracking
}
```

Always set `OrderBy` when using `Skip`/`MaxRows` — SQL gives no ordering guarantee
without an ORDER BY, so pages can otherwise repeat or miss rows.

### OrderingBuilder

```csharp
namespace FabrCore.Services.DataIntelligence.Query;

public static class OrderingBuilder
{
    // Orders by a schema-validated column name; throws ArgumentException listing
    // valid columns on an unknown name or navigation property.
    public static IQueryable<T> ApplyOrderBy<T>(
        IQueryable<T> query, string column, bool descending = false) where T : class;
}
```

### ProjectedQueryExecutor

```csharp
namespace FabrCore.Services.DataIntelligence.Query;

public static class ProjectedQueryExecutor
{
    // Schema: column names + types, zero data, zero DB queries
    public static QueryResult GetSchema<T>() where T : class;

    // Preview: small sample + total count (2 DB queries)
    public static Task<QueryResult> PreviewAsync<T>(
        IQueryable<T> baseQuery,
        IEnumerable<SpecificationGroup>? specificationGroups,
        ISpecificationFactory<T> factory,
        ColumnProjection? projection = null,
        int sampleSize = 3,
        CancellationToken cancellationToken = default) where T : class;

    // Full: projected, paginated, budget-aware (1-2 DB queries)
    public static Task<QueryResult> ExecuteAsync<T>(
        IQueryable<T> baseQuery,
        IEnumerable<SpecificationGroup>? specificationGroups,
        ISpecificationFactory<T> factory,
        QueryOptions options,
        CancellationToken cancellationToken = default) where T : class;
}
```

### QueryExecutor (Original)

```csharp
namespace FabrCore.Services.DataIntelligence.Query;

public static class QueryExecutor
{
    // Returns full entity List<T> (no projection, no budget).
    // Runs with AsNoTracking unless trackChanges is true.
    public static Task<List<T>> ExecuteAsync<T>(
        IQueryable<T> baseQuery,
        IEnumerable<SpecificationGroup>? specificationGroups,
        ISpecificationFactory<T> factory,
        CancellationToken cancellationToken = default,
        bool trackChanges = false) where T : class;
}
```

## Results

### QueryMode

```csharp
public enum QueryMode { Schema, Preview, Full }
```

### QueryResultMetadata

```csharp
public class QueryResultMetadata
{
    public string EntityType { get; init; }
    public List<string> Columns { get; init; }
    public int TotalPropertyCount { get; init; }
    public int? TotalMatchingRows { get; init; }  // From COUNT query, null if not requested
    public int RowsReturned { get; init; }
    public bool WasTruncated { get; init; }
    public QueryMode Mode { get; init; }
    public int SerializedCharacters { get; set; }
}
```

### QueryResult

```csharp
public class QueryResult
{
    public QueryResultMetadata Metadata { get; init; }
    public List<Dictionary<string, object?>> Data { get; init; }
    public EntitySchemaInfo? Schema { get; init; }       // Non-null in Schema mode
    public string SerializedJson { get; init; }           // Pre-built JSON for LLM
}
```

### ResultSerializer

```csharp
namespace FabrCore.Services.DataIntelligence.Results;

public static class ResultSerializer
{
    // Budget-aware serialization with null/default cleaning
    public static QueryResult BuildResult(
        List<Dictionary<string, object?>> allRows,
        IReadOnlyList<string> columns,
        EntitySchemaInfo schema,
        int? totalCount,
        QueryOptions options,
        QueryMode mode);

    // Schema-only result (no data rows)
    public static QueryResult BuildSchemaResult(EntitySchemaInfo schema);
}
```

**Budget enforcement rules:**
- At least 1 row is always included (even if it exceeds budget)
- Budget applies to cumulative serialized row JSON length
- When budget hit: `WasTruncated = true`, remaining rows dropped
- `TotalMatchingRows` still reflects the full COUNT for context

**Value cleaning:**
- `OmitNullValues`: removes `"prop": null` entries
- `OmitDefaultValues`: removes zero for all numeric types (int/long/decimal/double/float/short/ushort/byte/sbyte/uint/ulong), `false`, `Guid.Empty`, `DateTime.MinValue`, `DateTimeOffset.MinValue`, `DateOnly.MinValue`, `TimeOnly.MinValue`, `TimeSpan.Zero`
- Metadata keys serialize camelCase; **data row keys keep PascalCase property names** (dictionary keys aren't renamed), matching `metadata.columns` exactly
- No indentation, no null properties

## Unified Context

### EntityIntelligence

```csharp
namespace FabrCore.Services.DataIntelligence.Context;

public record EntityIntelligence(
    EntitySchemaInfo Schema,
    IReadOnlyList<SpecificationInfo> AvailableFilters);
```

### DataIntelligenceContext

```csharp
public class DataIntelligenceContext
{
    public DataIntelligenceContext(
        IEntitySchemaService schemaService,
        ISpecificationMetadataService metadataService);

    // Get combined schema + specs
    public EntityIntelligence GetEntityIntelligence<T>() where T : class;
    public EntityIntelligence? GetEntityIntelligence(string entityTypeName);
    public IReadOnlyList<EntityIntelligence> GetAllEntityIntelligence();

    // Format for system prompt (markdown, all entities)
    public string FormatForSystemPrompt();
    public string FormatForSystemPrompt<T>() where T : class;
    public string FormatForSystemPrompt(string entityTypeName);

    // Format for message injection (compact, single entity)
    public string FormatForMessage(string entityTypeName);
    public string FormatForMessage<T>() where T : class;
}
```

## Specification Factory

### ISpecificationFactory<T>

```csharp
public interface ISpecificationFactory<T>
{
    ISpecification<T> Create(string name, Dictionary<string, object?> parameters);
    ISpecification<T> CreateComposite(IEnumerable<SpecificationParameter> specifications);
    ISpecification<T> CreateFromGroups(IEnumerable<SpecificationGroup> groups);
    IReadOnlyList<SpecificationInfo> GetAvailableSpecifications();
}
```

### SpecificationFactory<T>

```csharp
// Constructor: scans assembly for [SpecificationFor<T>] classes
public SpecificationFactory(Assembly assembly);
public SpecificationFactory(IEnumerable<Assembly> assemblies);
```

**Discovery and creation behavior:**
- Duplicate names for the same entity throw `InvalidOperationException` at construction (same class scanned twice is deduplicated silently)
- When a spec has multiple public constructors, the one with the **most parameters** is used (deterministic tie-break)
- Explicit `null` parameter values bind to nullable parameter types; non-nullable parameters fall back to their default value or throw with a clear message
- Value conversion is invariant-culture; numbers sent as JSON strings (`"5"`) parse leniently; enums parse case-insensitively by name
- Assemblies with partially loadable types (`ReflectionTypeLoadException`) are scanned for the types that did load

### DI Registration (AddDataIntelligence)

```csharp
namespace FabrCore.Services.DataIntelligence.Configuration;

public static class DataIntelligenceServiceExtensions
{
    // Registers ISpecificationFactory<T> per entity (singleton), IEntitySchemaService,
    // ISpecificationMetadataService, and DataIntelligenceContext.
    public static IServiceCollection AddDataIntelligence(
        this IServiceCollection services,
        Action<DataIntelligenceBuilder> configure);
}

public class DataIntelligenceBuilder
{
    public DataIntelligenceBuilder AddEntity<TEntity>() where TEntity : class;
    public DataIntelligenceBuilder FromAssembly(Assembly assembly);
    public DataIntelligenceBuilder FromAssemblyOf<TMarker>();
}
```

When no `FromAssembly`/`FromAssemblyOf` is configured, the registered entity types' own assemblies are scanned. Throws if no entity is registered.

### SpecificationMetadataService

```csharp
public class SpecificationMetadataService : ISpecificationMetadataService
{
    public SpecificationMetadataService(IEnumerable<object> factories);

    // Convenience factory methods for 1-4 typed factories
    public static SpecificationMetadataService Create<T1>(ISpecificationFactory<T1> f1);
    public static SpecificationMetadataService Create<T1, T2>(...);
    public static SpecificationMetadataService Create<T1, T2, T3>(...);
    public static SpecificationMetadataService Create<T1, T2, T3, T4>(...);
}
```

### SpecificationInfo

```csharp
public record SpecificationInfo(
    string Name,          // "OrderByStatus"
    string EntityType,    // "Order"
    string Description,   // "Filters orders by status"
    IReadOnlyList<SpecificationParameterInfo> Parameters);

public record SpecificationParameterInfo(
    string Name,          // "status"
    string Type,          // "OrderStatus"
    bool Required,        // true
    string? Description); // "The order status to filter by"
```

## Parameters

### SpecificationGroup

```csharp
public class SpecificationGroup
{
    public LogicalOperator Operator { get; set; }  // And or Or
    public List<SpecificationParameter> Specifications { get; set; }
}

public enum LogicalOperator { And, Or }
```

### SpecificationParameter

```csharp
public class SpecificationParameter
{
    public string Name { get; set; }
    public Dictionary<string, object?>? ExplicitParameters { get; set; }
    [JsonExtensionData]
    public Dictionary<string, object?> ExtensionParameters { get; set; }
    public Dictionary<string, object?> Parameters { get; }  // Merged (explicit wins)
}
```

## Specification Composition

```csharp
// Extension methods on ISpecification<T>
public static ISpecification<T> And<T>(this ISpecification<T> left, ISpecification<T> right);
public static ISpecification<T> Or<T>(this ISpecification<T> left, ISpecification<T> right);
public static ISpecification<T> Not<T>(this ISpecification<T> spec);

// Public match-everything spec — neutral starting point for loop-built compositions
public sealed class TrueSpecification<T> : BaseSpecification<T>;
```

Composition rebinds only each side's own lambda parameter — criteria containing nested
lambdas (`o => o.Lines.Any(l => l.Qty > 5)`) compose correctly. Includes from both
sides are merged and de-duplicated.

## Hierarchical Queries

### EntityHierarchy<TRoot>

```csharp
EntityHierarchy<TRoot>.Create()
    .HasMany<TChild>(Expression<Func<TRoot, IEnumerable<TChild>>>)
        .ThenHasMany<TGrandChild>(Expression<Func<TChild, IEnumerable<TGrandChild>>>)
    .Build();
```

### HierarchicalQueryExecutor<TRoot>

```csharp
var executor = new HierarchicalQueryExecutor<TRoot>(hierarchy, rootFactory)
    .WithChildFactory<TChild>(childFactory)
    .WithChildFactory<TGrandChild>(grandChildFactory);

// Runs with AsNoTracking unless trackChanges is true. Cascade filtering mutates
// loaded child collections in memory — never enable tracking casually, or a later
// SaveChanges will treat the removals as relationship deletes.
var results = await executor.ExecuteAsync(
    IQueryable<TRoot> baseQuery,
    HierarchicalSpecifications specs,
    CancellationToken ct,
    bool trackChanges = false);
```

Hierarchies deeper than grandchild throw `NotSupportedException` at `Build()`.
Includes load only the levels being filtered: child filters load child collections,
grandchild filters load both.

### HierarchicalSpecifications

```csharp
public class HierarchicalSpecifications
{
    public IEnumerable<SpecificationGroup>? RootSpecifications { get; set; }
    public IEnumerable<SpecificationGroup>? ChildSpecifications { get; set; }
    public IEnumerable<SpecificationGroup>? GrandChildSpecifications { get; set; }
}
```
