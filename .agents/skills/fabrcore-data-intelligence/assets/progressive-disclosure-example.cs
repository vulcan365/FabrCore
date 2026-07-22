using FabrCore.Services.DataIntelligence.Context;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Projection;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Services.DataIntelligence.Parameters;

/// <summary>
/// Demonstrates the progressive disclosure pattern for token-efficient data queries.
/// Each step reveals more data while keeping token usage minimal.
/// </summary>
public class ProgressiveDisclosureExample
{
    private readonly AppDbContext _dbContext;
    private readonly ISpecificationFactory<Order> _orderFactory;
    private readonly DataIntelligenceContext _diContext;

    public ProgressiveDisclosureExample(
        AppDbContext dbContext,
        ISpecificationFactory<Order> orderFactory,
        DataIntelligenceContext diContext)
    {
        _dbContext = dbContext;
        _orderFactory = orderFactory;
        _diContext = diContext;
    }

    /// <summary>
    /// Step 0: System prompt injection — schema + filter awareness.
    /// Called once in agent OnInitialize. ~1-2K tokens per entity.
    /// </summary>
    public string InjectSystemPromptContext()
    {
        return _diContext.FormatForSystemPrompt();
        // Output:
        // ## Available Data Queries
        //
        // ### Order (47 columns)
        // **Columns**: Id (Guid), OrderNumber (string), Status (OrderStatus), ...
        // **Filters**:
        // - OrderById(orderId: Guid) — Filters orders by unique identifier
        // - OrderByStatus(status: OrderStatus) — Filters orders by status
        // ...
    }

    /// <summary>
    /// Step 1: Schema discovery — all column names + types, zero data.
    /// 0 DB queries. ~2K tokens. LLM uses this to pick columns.
    /// </summary>
    public string DiscoverSchema()
    {
        var result = ProjectedQueryExecutor.GetSchema<Order>();
        return result.SerializedJson;
        // Output: { metadata: { entityType: "Order", totalPropertyCount: 47, mode: "Schema" },
        //           schema: { scalarColumns: [{ name: "Id", type: "Guid", isNullable: false },
        //                                     { name: "Status", type: "OrderStatus",
        //                                       allowedValues: ["Pending","Shipped","Delivered"] }, ...],
        //                     navigationProperties: ["Lines", "Customer"] } }
    }

    /// <summary>
    /// Step 2: Preview — 3 sample rows with selected columns + total count.
    /// 2 DB queries (COUNT + SELECT TOP 3). ~500 tokens.
    /// LLM checks data shape and sees "1,247 total matching" to decide next step.
    /// </summary>
    public async Task<string> PreviewData()
    {
        var specs = new List<SpecificationGroup>
        {
            new()
            {
                Operator = LogicalOperator.And,
                Specifications =
                [
                    new SpecificationParameter { Name = "OrderByStatus", ExplicitParameters = new() { ["status"] = "Pending" } }
                ]
            }
        };

        var result = await ProjectedQueryExecutor.PreviewAsync(
            _dbContext.Orders, // AsNoTracking applied automatically
            specs,
            _orderFactory,
            ColumnProjection.For("Id", "OrderNumber", "Status", "Total", "OrderDate"),
            sampleSize: 3);

        return result.SerializedJson;
        // Output: { metadata: { entityType: "Order", columns: ["Id","OrderNumber","Status","Total","OrderDate"],
        //                        totalMatchingRows: 1247, rowsReturned: 3, wasTruncated: false, mode: "Preview" },
        //           data: [ { "Id": "...", "OrderNumber": "ORD-001", "Status": "Pending", "Total": 150.00, "OrderDate": "2025-03-15" },
        //                   { "Id": "...", "OrderNumber": "ORD-002", ... }, ... ] }
        // Note: metadata keys are camelCase; data row keys keep the entity's PascalCase
        // property names (they match metadata.columns exactly).
    }

    /// <summary>
    /// Step 3: Full query — budget-aware paginated results.
    /// 1-2 DB queries. Token cost hard-capped by CharacterBudget.
    /// </summary>
    public async Task<string> FullQuery()
    {
        var specs = new List<SpecificationGroup>
        {
            new()
            {
                Operator = LogicalOperator.And,
                Specifications =
                [
                    new SpecificationParameter { Name = "OrderByStatus", ExplicitParameters = new() { ["status"] = "Pending" } },
                    new SpecificationParameter { Name = "OrderDateRange", ExplicitParameters = new() { ["startDate"] = "2025-01-01" } }
                ]
            }
        };

        var result = await ProjectedQueryExecutor.ExecuteAsync(
            _dbContext.Orders, // AsNoTracking applied automatically (TrackChanges = false)
            specs,
            _orderFactory,
            new QueryOptions
            {
                Projection = ColumnProjection.For("Id", "OrderNumber", "Status", "Total", "OrderDate"),
                OrderBy = "OrderDate",          // stable ordering for repeatable results
                OrderByDescending = true,
                MaxRows = 50,
                CharacterBudget = 30000,
                OmitNullValues = true,
                OmitDefaultValues = true
            });

        // result.Metadata.WasTruncated — did we hit the budget?
        // result.Metadata.TotalMatchingRows — total rows matching (for pagination context)
        // result.Metadata.RowsReturned — actual rows in response

        return result.SerializedJson;
    }

    /// <summary>
    /// Pagination: requesting the next page of results.
    /// </summary>
    public async Task<string> NextPage(int skip)
    {
        var result = await ProjectedQueryExecutor.ExecuteAsync(
            _dbContext.Orders,
            null, // no filters = all orders
            _orderFactory,
            new QueryOptions
            {
                Projection = ColumnProjection.For("Id", "OrderNumber", "Status"),
                OrderBy = "Id",           // REQUIRED for stable paging — same order every page
                MaxRows = 20,
                Skip = skip,
                CharacterBudget = 15000,
                IncludeTotalCount = false // Already know total from first query
            });

        return result.SerializedJson;
    }

    /// <summary>
    /// Per-message context injection — compact format for specific turns.
    /// </summary>
    public string InjectMessageContext()
    {
        return _diContext.FormatForMessage("Order");
        // Output: [Order] Columns (47): Id, OrderNumber, Status, Total, OrderDate, ...
        //         Filters: OrderById(orderId), OrderByStatus(status), OrderDateRange(startDate, endDate), ...
    }
}
