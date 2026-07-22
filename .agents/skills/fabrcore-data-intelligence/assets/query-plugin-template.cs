using System.ComponentModel;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Parameters;
using FabrCore.Services.DataIntelligence.Projection;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Services.DataIntelligence.Results;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Example query plugin that exposes schema discovery, preview, and projected query tools.
/// Register as a plugin on your agent to give the LLM data query capabilities.
/// </summary>
[PluginAlias("data-query")]
public class DataQueryPlugin : IFabrCorePlugin
{
    private AppDbContext _dbContext = null!;
    private ISpecificationFactory<Order> _orderFactory = null!;

    public async Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _dbContext = serviceProvider.GetRequiredService<AppDbContext>();
        _orderFactory = serviceProvider.GetRequiredService<ISpecificationFactory<Order>>();
    }

    /// <summary>
    /// Discovers the schema of the Order entity — column names, types, and nullability.
    /// Call this first to understand what columns are available before querying data.
    /// </summary>
    [Description("Get the schema (column names and types) for Order data. Returns zero data rows.")]
    public string GetOrderSchema()
    {
        var result = ProjectedQueryExecutor.GetSchema<Order>();
        return result.SerializedJson;
    }

    /// <summary>
    /// Previews a small sample of orders matching the given filters, with total count.
    /// Use this to verify data shape and check result set size before a full query.
    /// </summary>
    [Description("Preview 3 sample orders matching filters, with total count. Use to check data before a full query.")]
    public async Task<string> PreviewOrders(
        [Description("Column names to include (e.g., 'Id,OrderNumber,Status')")] string? columns = null,
        [Description("JSON array of specification groups for filtering")] string? filters = null)
    {
        var projection = ParseColumns(columns);
        var specs = ParseFilters(filters);

        var result = await ProjectedQueryExecutor.PreviewAsync(
            _dbContext.Orders, // AsNoTracking applied automatically
            specs,
            _orderFactory,
            projection,
            sampleSize: 3);

        return result.SerializedJson;
    }

    /// <summary>
    /// Queries orders with column projection and budget-aware pagination.
    /// Always specify columns — requesting all columns on wide tables wastes tokens.
    /// </summary>
    [Description("Query orders with column projection. Specify columns to avoid returning all 600 columns.")]
    public async Task<string> QueryOrders(
        [Description("Column names to include (e.g., 'Id,OrderNumber,Status,Total')")] string columns,
        [Description("JSON array of specification groups for filtering")] string? filters = null,
        [Description("Column to sort by. Required for stable pagination.")] string orderBy = "Id",
        [Description("Sort descending (default: false)")] bool descending = false,
        [Description("Maximum rows to return (default: 50)")] int maxRows = 50,
        [Description("Character budget for response (default: 30000)")] int budget = 30000,
        [Description("Rows to skip for pagination")] int skip = 0)
    {
        var projection = ParseColumns(columns);
        var specs = ParseFilters(filters);

        // The executor applies AsNoTracking automatically (TrackChanges defaults to false)
        var result = await ProjectedQueryExecutor.ExecuteAsync(
            _dbContext.Orders,
            specs,
            _orderFactory,
            new QueryOptions
            {
                Projection = projection,
                OrderBy = orderBy,
                OrderByDescending = descending,
                MaxRows = maxRows,
                Skip = skip > 0 ? skip : null,
                CharacterBudget = budget,
                OmitNullValues = true,
                OmitDefaultValues = true
            });

        return result.SerializedJson;
    }

    private static ColumnProjection? ParseColumns(string? columns)
    {
        if (string.IsNullOrWhiteSpace(columns))
            return null;

        var colList = columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ColumnProjection.For(colList);
    }

    private static List<SpecificationGroup>? ParseFilters(string? filters)
    {
        if (string.IsNullOrWhiteSpace(filters))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<List<SpecificationGroup>>(filters);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
