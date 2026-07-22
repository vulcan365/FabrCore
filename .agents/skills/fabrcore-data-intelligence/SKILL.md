---
name: fabrcore-data-intelligence
description: This skill should be used when working with the FabrCore.Services.DataIntelligence specification pattern library for EF Core queries. Trigger phrases include "create a specification", "add specification", "specification pattern", "query filter", "EF Core specification", "specification factory", "compose specifications", "hierarchical query", "cascade filter", "specification template", "reporting service", "ByIdSpecification", "ContainsTextSpecification", "DateRangeSpecification", "SpecificationGroup", "specification metadata", "CascadeFilter", "QueryExecutor", "ProjectedQueryExecutor", "column projection", "query budget", "schema introspection", "DataIntelligenceContext", "FormatForSystemPrompt", "FormatForMessage", "EntitySchemaService", "EntitySchemaInfo", "ColumnProjection", "QueryOptions", "QueryResult", "ResultSerializer", "budget-aware query", "progressive disclosure", "token budget", "data agent", "DataSmeSystemPromptGuard", "SME consultation", "AddDataIntelligence", "DataIntelligenceBuilder", "OrderingBuilder", "OrderBy pagination", "TrackChanges", "TrueSpecification", "AllowedValues", "reporting platform", "build a reporting service", or when building dynamic, API-driven query filters against EF Core DbSets using the FabrCore library.
version: 0.4.0
---

# FabrCore.Services.DataIntelligence — Specification Pattern Query Engine

A .NET 10 library implementing the specification pattern for building composable, API-driven EF Core queries. Supports assembly-scanned factory discovery, fluent composition (And/Or/Not), hierarchical parent-child-grandchild queries with cascade filtering, a metadata system for runtime specification discovery, **column projection** (database-level SELECT of specific columns), **budget-aware serialization** (character budgets, null/default omission), and a **unified context service** for agent system prompt injection.

**Source**: `C:\repos\FabrCore-V365\src\FabrCore.Services.DataIntelligence`
**Dependencies**: `Microsoft.EntityFrameworkCore 10.0.5`, `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5`

> **Note**: This library was formerly called `FabrCore.Data.Intelligence` (and before that `Fabr.ReportingService.EntityFramework`). All namespaces are now under `FabrCore.Services.DataIntelligence`.

The library works **against your existing EF Core model** — you pass your own `DbSet<T>` queryables and entity classes. It defines no model, requires no DTOs (the projected path returns dictionaries + pre-serialized JSON), and needs no configuration beyond DI registration.

## Skill Resources

| Resource | Use For |
|----------|---------|
| `references/api-reference.md` | Complete API signatures for every public type |
| `references/architecture.md` | Execution pipeline, SQL patterns, budget/cleaning internals |
| `references/pitfalls.md` | 23 numbered pitfalls with problem/fix pairs |
| `assets/reporting-service-template.cs` | Full reporting platform: specs → DI → service → API controller |
| `assets/data-query-agent-template.cs` | LLM data agent with schema-aware system prompt |
| `assets/query-plugin-template.cs` | Plugin exposing schema/preview/query tools to an LLM |
| `assets/progressive-disclosure-example.cs` | Schema → Preview → Full token-efficient query flow |
| `assets/di-registration.cs` | One-call and manual DI registration variants |

## Getting Started — Build Out a Reporting Platform

Follow these steps in order. Each step compiles and is testable before the next.

**Step 1 — Reference the package and your EF Core model.** DataIntelligence sits on top of your existing `DbContext`; no model changes needed.

**Step 2 — Write specifications for each entity you want filterable.** Start from templates (`ByIdSpecification`, `DateRangeSpecification`, `ContainsTextSpecification`, ...) — one small class per filter, marked `[SpecificationFor<T>]` with `[SpecificationDescription]`. See *Creating Specifications* below.

**Step 3 — Register services with one call:**

```csharp
services.AddDataIntelligence(di => di
    .AddEntity<Order>()
    .AddEntity<OrderLine>()
    .FromAssemblyOf<OrderByIdSpecification>());
```

**Step 4 — Build a reporting service** that accepts `SpecificationGroup` lists and executes them via `QueryExecutor` (full entities → DTOs) or `ProjectedQueryExecutor` (dictionaries + JSON for LLMs). Complete template: `assets/reporting-service-template.cs`.

**Step 5 — Expose it.** For a REST API, add search endpoints plus metadata discovery endpoints (`GetAvailableSpecifications`) so clients can discover filters at runtime. For an agent, inject `DataIntelligenceContext.FormatForSystemPrompt()` and expose query tools (`assets/query-plugin-template.cs`).

**Step 6 — For LLM consumers, follow progressive disclosure**: Schema (0 queries) → Preview (3 rows + count) → Full (budget-capped). Never return unprojected entities to an LLM.

**Step 7 — Add hierarchical filtering only when needed** (parent/child/grandchild cascade) — see *Hierarchical Queries* below.

**Step 8 — Read `references/pitfalls.md`** before shipping; it covers the mistakes that pass code review but fail in production (pagination without OrderBy, COUNT on unfiltered tables, OmitDefaultValues hiding real zeros).

## Architecture Overview

```
Specifications/
  ISpecification<T>              — Criteria (Expression<Func<T, bool>>) + Includes
  BaseSpecification<T>           — Abstract base, set Criteria in constructor
  SpecificationExtensions        — .And() .Or() .Not() fluent composition
  SpecificationEvaluator<T>      — Applies spec to IQueryable (Where + Include)
  Templates/                     — 9 abstract templates for common filter patterns

Factory/
  ISpecificationFactory<T>       — Create by name, compose, create from groups
  SpecificationFactory<T>        — Reflection-based discovery via [SpecificationFor<T>]

Metadata/
  [SpecificationFor<T>]          — Marks a class as spec for entity T
  [SpecificationDescription]     — User-friendly description text
  [SpecificationParameter]       — Documents constructor params (on properties)
  SpecificationMetadataService   — Aggregates metadata from multiple factories
  EntitySchemaService            — Reflection-based entity schema introspection
  EntitySchemaInfo / EntityPropertyInfo — Schema metadata records

Projection/
  ColumnProjection               — Column selection config (explicit list or all scalars)
  ProjectionBuilder              — Dynamic LINQ expression builder for EF Core SELECT

Query/
  QueryExecutor                  — Static ExecuteAsync for flat queries (returns List<T>)
  ProjectedQueryExecutor         — Schema/Preview/Full modes with projection + budget
  QueryOptions                   — Projection, ordering, pagination, character budget, null omission, tracking opt-in
  OrderingBuilder                — Validated name-based OrderBy/OrderByDescending
  CascadeFilter                  — In-memory child/grandchild filtering

Results/
  QueryResult                    — Envelope: metadata + data + schema + serialized JSON
  QueryResultMetadata            — Row/column counts, truncation status, budget usage
  ResultSerializer               — Budget-aware JSON serializer with value cleaning

Context/
  DataIntelligenceContext        — Unified schema + specs, FormatForSystemPrompt/Message
  EntityIntelligence             — Combined schema + filters for one entity type

Parameters/
  SpecificationParameter         — Name + merged parameters (JSON-friendly)
  SpecificationGroup             — LogicalOperator (And|Or) + list of specs

Hierarchy/
  EntityHierarchy<TRoot>         — Fluent builder: Create().HasMany().ThenHasMany().Build()
  HierarchicalSpecifications     — Root/Child/GrandChild spec groups
  HierarchicalQueryExecutor      — Orchestrates multi-level queries
```

## Agent Integration (DataIntelligenceContext)

The `DataIntelligenceContext` is the primary API for agents. It combines entity schema introspection and specification metadata into a single service with formatted output ready for system prompt injection.

### DI Registration

```csharp
using FabrCore.Services.DataIntelligence.Configuration;

// One call registers ISpecificationFactory<T> per entity, IEntitySchemaService,
// ISpecificationMetadataService, and DataIntelligenceContext.
services.AddDataIntelligence(di => di
    .AddEntity<Order>()
    .AddEntity<OrderLine>()
    .AddEntity<Product>()
    .FromAssemblyOf<OrderByIdSpecification>());
```

Omitting `FromAssembly`/`FromAssemblyOf` scans the entity types' own assemblies.
Manual registration (`services.AddSingleton<ISpecificationFactory<Order>>(...)` etc.)
still works when finer control is needed — see `assets/di-registration.cs`.

### System Prompt Injection

```csharp
// In agent OnInitialize — inject schema + filter awareness
var diContext = serviceProvider.GetRequiredService<DataIntelligenceContext>();
config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt();
```

Output format:
```markdown
## Available Data Queries

### Order (47 columns)
**Columns**: Id (Guid), OrderNumber (string), Status (OrderStatus: Pending|Shipped|Delivered), OrderDate (DateTime), ...
**Filters**:
- OrderById(orderId: Guid) — Filters orders by unique identifier
- OrderByStatus(status: OrderStatus) — Filters orders by status
- OrderDateRange(startDate?: DateTime, endDate?: DateTime) — Filters within date range
```

### Per-Message Context Injection

```csharp
// More compact format for injecting on specific messages
var context = diContext.FormatForMessage("Order");
// Output: [Order] Columns (47): Id, OrderNumber, Status, ...
//         Filters: OrderById(orderId), OrderByStatus(status), ...
```

### API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `GetEntityIntelligence<T>()` | `EntityIntelligence` | Schema + specs for one entity type |
| `GetEntityIntelligence(name)` | `EntityIntelligence?` | Schema + specs by entity type name |
| `GetAllEntityIntelligence()` | `IReadOnlyList<EntityIntelligence>` | All registered entities |
| `FormatForSystemPrompt()` | `string` | Markdown for all entities (system prompt) |
| `FormatForSystemPrompt<T>()` | `string` | Markdown for one entity (system prompt) |
| `FormatForSystemPrompt(name)` | `string` | Markdown for one entity by name |
| `FormatForMessage(name)` | `string` | Compact format for per-message injection |
| `FormatForMessage<T>()` | `string` | Compact format for per-message injection |

## Schema Introspection

Discover entity columns without fetching data. Zero database queries.

```csharp
using FabrCore.Services.DataIntelligence.Metadata;

var schemaService = new EntitySchemaService(typeof(Order), typeof(Product));

// Get schema for a type
var schema = schemaService.GetSchema<Order>();
// schema.EntityType = "Order"
// schema.PropertyCount = 50
// schema.ScalarPropertyNames = ["Id", "OrderNumber", "Status", ...]
// schema.NavigationPropertyNames = ["Lines", "Customer"]
// schema.Properties = [EntityPropertyInfo(Name, Type, IsNullable, IsNavigation), ...]

// Get schema by name
var productSchema = schemaService.GetSchema("Product");

// List registered entity types
var types = schemaService.GetEntityTypes(); // ["Order", "Product"]
```

Each `EntityPropertyInfo` includes:
- `Name` — property name
- `Type` — friendly type string ("string", "int", "DateTime?", "Guid")
- `IsNullable` — whether the property accepts null
- `IsNavigation` — whether this is a navigation/collection property (any non-simple class type, or a collection of one)
- `AllowedValues` — enum member names for enum properties, null otherwise

> **Scope note**: introspection reflects over the **CLR type**, not EF Core's `IModel`. A `[NotMapped]` property still appears as a column (projecting it fails at SQL translation), and shadow properties don't appear at all. Keep reportable entities free of unmapped public properties.

## Projected Queries (Budget-Aware)

### The Problem

A query returning 50 rows × 600 columns = 200K+ tokens. Row limits don't help when each row is enormous.

### The Solution: Progressive Disclosure

Three query modes let the LLM discover what it needs without blowing up the token budget:

```csharp
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Services.DataIntelligence.Projection;

// Step 1: Schema — discover columns (0 DB queries, ~2K tokens)
var schema = ProjectedQueryExecutor.GetSchema<Order>();
// Returns column names + types so LLM can pick what it needs

// Step 2: Preview — small sample + total count (2 DB queries, ~500 tokens)
var preview = await ProjectedQueryExecutor.PreviewAsync(
    dbContext.Orders,          // AsNoTracking applied automatically
    orderGroups,
    orderFactory,
    ColumnProjection.For("Id", "OrderNumber", "Status", "Total"),
    sampleSize: 3);
// preview.Metadata.TotalMatchingRows = 1247
// preview.Data = [3 rows with only 4 columns each]

// Step 3: Full query — budget-aware (1-2 DB queries, hard-capped tokens)
var result = await ProjectedQueryExecutor.ExecuteAsync(
    dbContext.Orders,
    orderGroups,
    orderFactory,
    new QueryOptions
    {
        Projection = ColumnProjection.For("Id", "OrderNumber", "Status", "Total"),
        OrderBy = "OrderNumber",       // always set when paginating — DBs give no default order
        MaxRows = 50,
        CharacterBudget = 30000,
        OmitNullValues = true,
        OmitDefaultValues = true
    });

// result.SerializedJson — ready to return to LLM
// result.Metadata.WasTruncated — true if budget was hit
// result.Metadata.TotalMatchingRows — total matching for context
```

### Column Projection

Column projection happens at the **database level** (SQL SELECT), not in-memory:

```csharp
// Explicit columns
var projection = ColumnProjection.For("Id", "OrderNumber", "Status");

// All scalar columns (excludes navigation properties)
var projection = ColumnProjection.AllScalars();

// Column names are validated case-insensitively
// Invalid columns throw ArgumentException with the list of valid column names
```

### QueryOptions

| Property | Default | Description |
|----------|---------|-------------|
| `Projection` | null (all scalars) | Which columns to include |
| `OrderBy` | null | Column to sort by (case-insensitive, validated). **Set this whenever `Skip`/`MaxRows` is used** — without it page contents are nondeterministic |
| `OrderByDescending` | false | Sort direction for `OrderBy` |
| `MaxRows` | null (no limit) | Maximum rows to return |
| `Skip` | null (start at 0) | Rows to skip for pagination |
| `IncludeTotalCount` | true | Execute COUNT query for total matching |
| `CharacterBudget` | null (no budget) | Max characters for serialized data |
| `OmitNullValues` | true | Remove null values from JSON |
| `OmitDefaultValues` | true | Remove 0, false, empty Guid, MinValue dates |
| `TrackChanges` | false | Queries run with `AsNoTracking` by default; opt in only to modify + save results |

### QueryResult Envelope

Every query returns a `QueryResult` with:

```csharp
result.Metadata.EntityType        // "Order"
result.Metadata.Columns           // ["Id", "OrderNumber", "Status"]
result.Metadata.TotalPropertyCount // 600 (total on entity)
result.Metadata.TotalMatchingRows // 1247 (from COUNT query)
result.Metadata.RowsReturned      // 47 (actual rows in Data)
result.Metadata.WasTruncated      // true (budget hit before all rows)
result.Metadata.Mode              // Full, Preview, or Schema
result.Data                       // List<Dictionary<string, object?>>
result.SerializedJson             // Pre-built JSON string for LLM
result.Schema                     // EntitySchemaInfo (Schema mode only)
```

### Token Savings

| Scenario | Est. Tokens |
|----------|-------------|
| No projection (600 cols, 50 rows) | 200,000+ |
| System prompt with schema + filters | ~1,000-2,000 |
| Schema-only query | ~2,000 |
| Preview (3 rows, 10 cols) | ~500 |
| Full (50 rows, 10 cols, null omission) | ~3,000-4,000 |
| Full with 30K char budget | Hard cap ~7,500 |

### ProjectedQueryExecutor API

| Method | Purpose | DB Queries |
|--------|---------|------------|
| `GetSchema<T>()` | Column names + types, zero data | 0 |
| `PreviewAsync<T>(query, specs, factory, projection?, sampleSize)` | Small sample + total count | 2 |
| `ExecuteAsync<T>(query, specs, factory, options)` | Full projected, budget-aware | 1-2 |

## Data Query Agent Template

Complete agent combining schema-aware system prompt injection with budget-aware projected queries:

```csharp
using FabrCore.Services.DataIntelligence.Agents;
using FabrCore.Services.DataIntelligence.Context;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Projection;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

[AgentAlias("data-query-agent")]
public class DataQueryAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public DataQueryAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Inject schema + filter awareness into system prompt
        var diContext = serviceProvider.GetRequiredService<DataIntelligenceContext>();
        config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt();

        // 2. SME guard — prevents this read-only agent from claiming swarm-wide
        //    capability gaps during planner consultations (see pitfalls.md #19)
        config.SystemPrompt += "\n\n" + DataSmeSystemPromptGuard.Text;

        // 3. Set up tools (including query tools from your plugin)
        var tools = await ResolveConfiguredToolsAsync();

        // 4. Create LLM agent
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }
}
```

## Creating Specifications

### Using Templates (Recommended)

Templates handle expression tree construction. You provide the property selector. Use `[SpecificationParameter]` on properties to document parameters - the property name must match the constructor parameter name (case-insensitive). You can alias base class properties for clearer names:

```csharp
using FabrCore.Services.DataIntelligence.Metadata;
using FabrCore.Services.DataIntelligence.Specifications.Templates;

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders by their unique identifier")]
public class OrderByIdSpecification : ByIdSpecification<Order, Guid>
{
    [SpecificationParameter("The unique identifier of the order", Required = true)]
    public Guid OrderId => Id;  // Alias base property for better API metadata

    public OrderByIdSpecification(Guid orderId) : base(orderId, o => o.Id) { }
}

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders by status")]
public class OrderByStatusSpecification : ByEnumSpecification<Order, OrderStatus>
{
    [SpecificationParameter("The order status to filter by", Required = true)]
    public OrderStatus Status => EnumValue;

    public OrderByStatusSpecification(OrderStatus status) : base(status, o => o.Status) { }
}

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders within a date range")]
public class OrderDateRangeSpecification : DateRangeSpecification<Order>
{
    [SpecificationParameter("Start date (inclusive)", Required = false)]
    public new DateTime? StartDate => base.StartDate;

    [SpecificationParameter("End date (inclusive)", Required = false)]
    public new DateTime? EndDate => base.EndDate;

    public OrderDateRangeSpecification(DateTime? startDate = null, DateTime? endDate = null)
        : base(startDate, endDate, o => o.OrderDate) { }
}

[SpecificationFor<OrderLine>]
[SpecificationDescription("Filters order lines by product SKU")]
public class OrderLineBySkuSpecification : ByStringSpecification<OrderLine>
{
    [SpecificationParameter("The product SKU to match", Required = true)]
    public string ProductSku => Value;

    public OrderLineBySkuSpecification(string productSku) : base(productSku, l => l.ProductSku) { }
}

[SpecificationFor<Product>]
[SpecificationDescription("Search products by name")]
public class ProductNameContainsSpecification : ContainsTextSpecification<Product>
{
    [SpecificationParameter("Text to search for", Required = true)]
    public string SearchText { get; }

    public ProductNameContainsSpecification(string searchText) : base(searchText, p => p.Name) { }
}

[SpecificationFor<Product>]
[SpecificationDescription("Filter products by price range")]
public class ProductPriceRangeSpecification : NumericRangeSpecification<Product, decimal>
{
    [SpecificationParameter("Minimum price")]
    public decimal? MinValue { get; }

    [SpecificationParameter("Maximum price")]
    public decimal? MaxValue { get; }

    public ProductPriceRangeSpecification(decimal? minValue = null, decimal? maxValue = null)
        : base(minValue, maxValue, p => p.Price) { }
}

[SpecificationFor<Product>]
[SpecificationDescription("Filter products by category")]
public class ProductByCategorySpecification : ByForeignKeySpecification<Product, Guid>
{
    [SpecificationParameter("The category ID", Required = true)]
    public Guid CategoryId => ForeignKeyValue;

    public ProductByCategorySpecification(Guid categoryId) : base(categoryId, p => p.CategoryId) { }
}

[SpecificationFor<Product>]
[SpecificationDescription("Filter by active status")]
public class ProductIsActiveSpecification : ByBoolSpecification<Product>
{
    [SpecificationParameter("Active status", Required = true)]
    public bool IsActive => Value;

    public ProductIsActiveSpecification(bool isActive) : base(isActive, p => p.IsActive) { }
}
```

### Using BaseSpecification Directly

For complex or custom filter logic that doesn't fit a template:

```csharp
using FabrCore.Services.DataIntelligence.Metadata;
using FabrCore.Services.DataIntelligence.Specifications;

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders that are overdue")]
public class OrderOverdueSpecification : BaseSpecification<Order>
{
    [SpecificationParameter("Reference date (defaults to today)", Required = false)]
    public DateTime? AsOfDate { get; }

    public OrderOverdueSpecification(DateTime? asOfDate = null)
    {
        AsOfDate = asOfDate;
        var referenceDate = asOfDate ?? DateTime.UtcNow;

        Criteria = o =>
            o.DueDate.HasValue &&
            o.DueDate.Value < referenceDate &&
            o.Status != OrderStatus.Completed &&
            o.Status != OrderStatus.Cancelled;
    }
}
```

### Adding Includes

Call `AddInclude()` in the constructor to eagerly load navigation properties:

```csharp
[SpecificationFor<Order>]
public class OrderByIdWithDetailsSpecification : ByIdSpecification<Order, Guid>
{
    public OrderByIdWithDetailsSpecification(Guid id) : base(id, o => o.Id)
    {
        AddInclude(o => o.Lines);            // Typed include
        AddInclude("Lines.Product");          // String-based for nested paths
    }
}
```

## Available Templates Reference

| Template | Type Params | Constructor Args | Use Case |
|---|---|---|---|
| `ByIdSpecification<T, TKey>` | `TKey : struct` | `(TKey id, Expression selector)` | Primary key lookup |
| `ByStringSpecification<T>` | — | `(string value, Expression selector)` | Exact string match |
| `ByBoolSpecification<T>` | — | `(bool value, Expression selector)` | Boolean filter |
| `ByEnumSpecification<T, TEnum>` | `TEnum : struct, Enum` | `(TEnum value, Expression selector)` | Enum value match |
| `ByForeignKeySpecification<T, TKey>` | `TKey : struct` | `(TKey value, Expression selector)` | FK relationship |
| `ContainsTextSpecification<T>` | — | `(string text, Expression selector)` | Case-insensitive substring |
| `DateRangeSpecification<T>` | — | `(DateTime? start, DateTime? end, Expression selector)` | Non-nullable date range |
| `NullableDateRangeSpecification<T>` | — | `(DateTime? start, DateTime? end, Expression selector)` | Nullable DateTime range |
| `NumericRangeSpecification<T, TValue>` | `TValue : struct, IComparable<TValue>` | `(TValue? min, TValue? max, Expression selector)` | Numeric range |

## Factory Setup and DI Registration

The factory discovers specifications by scanning assemblies for classes with `[SpecificationFor<T>]`. Use `AddDataIntelligence` (see *DI Registration* above); manual per-service registration for finer control is shown in `assets/di-registration.cs`.

```csharp
// Multiple spec assemblies — call FromAssembly once per assembly
services.AddDataIntelligence(di => di
    .AddEntity<Order>()
    .AddEntity<Product>()
    .FromAssemblyOf<OrderByIdSpecification>()
    .FromAssembly(typeof(SharedSpecifications).Assembly));
```

### Specification Naming Rules

The factory strips the `Specification` suffix from the class name. Override with `Name` on the attribute:

- `OrderByStatusSpecification` -> `"OrderByStatus"`
- `[SpecificationFor<Order>(Name = "OverdueOrders")]` -> `"OverdueOrders"`

Names are **case-insensitive** for lookup via `factory.Create(name, params)`. Two different classes resolving to the same name for the same entity throw `InvalidOperationException` at factory construction — use `Name = "..."` to disambiguate.

## Implementing a Reporting Service

The recommended pattern combines `QueryExecutor` for DB-level filtering with `CascadeFilter` for in-memory child filtering, returning DTOs instead of entities. Queries run with `AsNoTracking` by default (pass `trackChanges: true` only to modify + save results — never when cascade filtering, which mutates child collections in memory). Full end-to-end template including the API controller: `assets/reporting-service-template.cs`.

```csharp
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Services.DataIntelligence.Parameters;

public class OrderReportingService
{
    private readonly AppDbContext _context;
    private readonly ISpecificationFactory<Order> _orderFactory;
    private readonly ISpecificationFactory<OrderLine> _lineFactory;

    private static readonly ChildCollectionAccessor<Order, OrderLine> OrderLinesAccessor = new()
    {
        GetChildren = o => o.Lines,
        SetChildren = (o, lines) => o.Lines = lines
    };

    public OrderReportingService(
        AppDbContext context,
        ISpecificationFactory<Order> orderFactory,
        ISpecificationFactory<OrderLine> lineFactory)
    {
        _context = context;
        _orderFactory = orderFactory;
        _lineFactory = lineFactory;
    }

    public async Task<List<OrderDto>> GetOrdersAsync(
        IEnumerable<SpecificationGroup>? orderGroups = null,
        IEnumerable<SpecificationGroup>? lineGroups = null,
        CancellationToken ct = default)
    {
        IQueryable<Order> baseQuery = _context.Orders;

        // Only include children when child filters are provided
        if (lineGroups?.Any() == true)
            baseQuery = baseQuery.Include(o => o.Lines);

        // Execute root query at DB level (AsNoTracking applied automatically)
        var orders = await QueryExecutor.ExecuteAsync(
            baseQuery, orderGroups, _orderFactory, ct);

        // Apply child filters in-memory (removes parents with no matches)
        orders = CascadeFilter.Apply(
            orders, lineGroups, _lineFactory, OrderLinesAccessor);

        return orders.Select(o => o.ToDto()).ToList();
    }
}
```

## Composing Specifications

### Programmatic composition

```csharp
var activeSpec = new ProductIsActiveSpecification(true);
var priceSpec = new ProductPriceRangeSpecification(minValue: 10m, maxValue: 100m);

var combined = activeSpec.And(priceSpec);              // Both must match
var either = activeSpec.Or(priceSpec);                 // Either matches
var notActive = activeSpec.Not();                      // Invert
var complex = activeSpec.And(priceSpec).Or(nameSpec);  // Chain freely
```

Composition is safe for criteria containing nested lambdas (e.g. `o => o.Lines.Any(l => l.Qty > 5)`) — inner lambda parameters are preserved. Includes from both sides are merged and de-duplicated. `TrueSpecification<T>` (public) is a match-everything neutral starting point for building compositions in loops.

### Factory-based composition (AND-chains a list)

```csharp
var spec = factory.CreateComposite(new[]
{
    new SpecificationParameter { Name = "ProductIsActive", ExplicitParameters = new() { ["isActive"] = true } },
    new SpecificationParameter { Name = "ProductPriceRange", ExplicitParameters = new() { ["minValue"] = 10m } }
});
```

## API-Driven Query Parameters

### JSON Request Structure

Parameters can be nested under `"parameters"` or flat alongside `"name"`:

```json
{
  "orderGroups": [
    {
      "operator": "And",
      "specifications": [
        { "name": "OrderByStatus", "status": "Pending" },
        { "name": "OrderDateRange", "startDate": "2025-01-01", "endDate": "2025-12-31" }
      ]
    }
  ],
  "lineGroups": [
    {
      "operator": "And",
      "specifications": [
        { "name": "OrderLineBySku", "productSku": "WIDGET-001" }
      ]
    }
  ]
}
```

| Concept | Description |
|---------|-------------|
| `operator` | `"And"` or `"Or"` — how specifications within a group combine |
| `specifications` | Array of specification objects with `name` and parameter properties |
| Multiple groups | Groups are combined with AND logic between them |

### Endpoint Pattern

```csharp
[HttpPost("orders/search")]
public async Task<IActionResult> SearchOrders(
    [FromBody] OrderSearchRequest request,
    CancellationToken ct)
{
    var results = await _reportingService.GetOrdersAsync(
        request.OrderGroups, request.LineGroups, ct);
    return Ok(results);
}

public class OrderSearchRequest
{
    public List<SpecificationGroup>? OrderGroups { get; set; }
    public List<SpecificationGroup>? LineGroups { get; set; }
}
```

### Metadata Discovery Endpoint

```csharp
[HttpGet("specifications")]
public IActionResult GetAll() => Ok(_metadataService.GetAll());

[HttpGet("specifications/{entityType}")]
public IActionResult GetForEntity(string entityType)
    => Ok(_metadataService.GetForEntity(entityType));

[HttpGet("specifications/entities")]
public IActionResult GetEntityTypes() => Ok(_metadataService.GetEntityTypes());
```

## Hierarchical Queries

For parent-child-grandchild queries using the `HierarchicalQueryExecutor`, which automates include depth and cascade filtering.

### Define the Hierarchy

```csharp
var hierarchy = EntityHierarchy<Order>.Create()
    .HasMany<OrderLine>(o => o.Lines)
        .ThenHasMany<OrderLineDetail>(ol => ol.Details)
    .Build();
```

### Execute with HierarchicalQueryExecutor

```csharp
var executor = new HierarchicalQueryExecutor<Order>(hierarchy, orderFactory)
    .WithChildFactory<OrderLine>(orderLineFactory)
    .WithChildFactory<OrderLineDetail>(detailFactory);

var results = await executor.ExecuteAsync(
    _dbContext.Orders, // AsNoTracking applied automatically
    new HierarchicalSpecifications
    {
        RootSpecifications = request.OrderFilters,
        ChildSpecifications = request.LineFilters,
        GrandChildSpecifications = request.DetailFilters
    },
    ct);
```

**Behavior**:
- Root specs execute as SQL WHERE clauses. Child/grandchild specs run in-memory via `CascadeFilter`. Parents with zero matching children after filtering are removed from results.
- Includes load only the levels being filtered: child filters load child collections; grandchild filters load both. No filters at a level = that level is not loaded.
- Runs with `AsNoTracking` by default (`trackChanges: false`). Never enable tracking with cascade filters — the in-memory child removal would be interpreted as relationship deletes on a later `SaveChanges`.
- Depth limit is grandchild; deeper `ThenHasMany` chains throw `NotSupportedException` at `Build()`.

## API Reference

| Class | Method | Description |
|-------|--------|-------------|
| `DataIntelligenceContext` | `FormatForSystemPrompt()` | Markdown with schema + filters for all entities |
| `DataIntelligenceContext` | `FormatForMessage(name)` | Compact context for per-message injection |
| `DataIntelligenceContext` | `GetEntityIntelligence<T>()` | Combined schema + specs for one entity |
| `EntitySchemaService` | `GetSchema<T>()` | Entity column metadata (names, types, nullability) |
| `ProjectedQueryExecutor` | `GetSchema<T>()` | Schema-only QueryResult (0 DB queries) |
| `ProjectedQueryExecutor` | `PreviewAsync(...)` | Sample rows + total count |
| `ProjectedQueryExecutor` | `ExecuteAsync(...)` | Full projected, budget-aware query |
| `SpecificationFactory<T>` | `Create(name, parameters)` | Creates a single specification instance |
| `SpecificationFactory<T>` | `CreateComposite(specs)` | AND-chains a list of SpecificationParameter objects |
| `SpecificationFactory<T>` | `CreateFromGroups(groups)` | Creates composite spec from groups (AND between groups) |
| `SpecificationFactory<T>` | `GetAvailableSpecifications()` | Returns metadata for all discovered specifications |
| `QueryExecutor` | `ExecuteAsync(query, groups, factory, ct)` | Applies specs as SQL WHERE and executes (returns List<T>) |
| `CascadeFilter` | `Apply(parents, groups, factory, accessor)` | Filters child collections, removes empty parents |
| `CascadeFilter` | `ApplyNested(parents, groups, factory, childAccessor, grandchildAccessor)` | Filters grandchild collections |
| `ResultSerializer` | `BuildResult(...)` | Budget-aware JSON serialization with value cleaning |
| `ResultSerializer` | `BuildSchemaResult(schema)` | Schema-only JSON output |

## Project Structure Example

```
YourDomain/
|-- Entities/
|   |-- Order.cs
|   +-- OrderLine.cs
|-- Reporting/
|   |-- Dtos/
|   |   |-- OrderDto.cs
|   |   +-- DtoMapper.cs
|   |-- Specifications/
|   |   |-- Order/
|   |   |   |-- OrderByIdSpecification.cs
|   |   |   +-- OrderByStatusSpecification.cs
|   |   +-- OrderLine/
|   |       +-- OrderLineBySkuSpecification.cs
|   |-- IOrderReportingService.cs
|   +-- OrderReportingService.cs
+-- Data/
    +-- AppDbContext.cs
```

## Best Practices

1. **Use `DataIntelligenceContext` for agents** — One DI registration, one method call for schema + specs
2. **Always project columns for LLM queries** — Never return 600 columns when the LLM needs 5
3. **Set character budgets** — Prevent runaway token usage with `QueryOptions.CharacterBudget`
4. **Use Preview before Full** — Let the LLM verify data shape and check total count first
5. **Use specification templates** — For common patterns, templates reduce code by 60-80%
6. **Keep specifications focused** — Each specification should do one thing well
7. **Use optional parameters** for range filters — Allow filtering by just start or just end
8. **Document parameters** — Use `[SpecificationParameter]` with clear descriptions
9. **Return DTOs, not entities** — For `QueryExecutor` (not needed for `ProjectedQueryExecutor` which projects to dictionaries)
10. **Include only when needed** — Only add `.Include()` when child filters are provided
11. **Name consistently** — Follow `{Entity}{Filter}Specification` naming convention
12. **Alias base properties** — Use `public Guid OrderId => Id;` for clearer API metadata names
13. **Always set `OrderBy` when paginating** — `Skip`/`MaxRows` without it produces nondeterministic pages
14. **Leave `TrackChanges` false** — queries are read-only by default; tracked entities + cascade filtering risks accidental relationship deletes on `SaveChanges`
15. **Keep reportable entities clean** — no `[NotMapped]` public properties; schema introspection reflects the CLR type, not the EF model

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `Unknown specification: X` | Add `[SpecificationFor<T>]` attribute to the class |
| `Required parameter 'X' not provided` | Check parameter names match constructor args (case-insensitive) |
| `Unknown column(s): X` | Column name doesn't match any property; error lists valid names |
| `Column 'X' is a navigation property` | Use the FK property instead (e.g., `CustomerId` not `Customer`) |
| Circular reference serialization error | Use `ProjectedQueryExecutor` (projects to dictionaries) or return DTOs |
| Specifications not discovered | Ensure classes are `public`, non-`abstract`, with `[SpecificationFor<T>]` |
| Child/grandchild filtering slow | Only `RootSpecifications` run as SQL; child filtering is in-memory |
| Default criteria matches everything | `BaseSpecification<T>.Criteria` defaults to `_ => true` — set it in your constructor |
| Name not resolving | Factory strips `Specification` suffix; override with `Name` on the attribute |
| Flat vs nested JSON params | `ExplicitParameters` = nested `"parameters": {}`, `ExtensionParameters` = flat via `[JsonExtensionData]`; merged `Parameters` property combines both |
| Budget not stopping rows | `CharacterBudget` always includes at least 1 row; check budget value vs row size |
| COUNT query slow on large tables | Set `IncludeTotalCount = false` in `QueryOptions` |
| `Duplicate specification name 'X'` | Two spec classes resolve to the same name; set `[SpecificationFor<T>(Name = "...")]` on one |
| Pages repeat or miss rows | Set `QueryOptions.OrderBy` — pagination without ORDER BY is nondeterministic |
| `[NotMapped]` property fails to project | Schema reflects the CLR type, not the EF model; remove or avoid projecting unmapped properties |
| Null passed for a non-nullable parameter | Error says which parameter; make the ctor parameter nullable or give it a default |
| Rows disappear after SaveChanges | Cascade filtering mutated tracked entities; keep the default `trackChanges: false` |
