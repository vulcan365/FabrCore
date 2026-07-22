// ============================================================================
// Complete reporting platform template: specifications → DI → reporting
// service → API controller with metadata discovery.
//
// Build order (each step compiles independently):
//   1. Specifications  2. DI registration  3. Reporting service
//   4. Search endpoints  5. Metadata discovery endpoints
// ============================================================================

using FabrCore.Services.DataIntelligence.Configuration;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Metadata;
using FabrCore.Services.DataIntelligence.Parameters;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Services.DataIntelligence.Specifications.Templates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================
// STEP 1 — Specifications (one small class per filter)
// ============================================================================

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders by status")]
public class OrderByStatusSpecification : ByEnumSpecification<Order, OrderStatus>
{
    [SpecificationParameter("The order status to filter by", Required = true)]
    public OrderStatus Status => EnumValue;

    public OrderByStatusSpecification(OrderStatus status) : base(status, o => o.Status) { }
}

[SpecificationFor<Order>]
[SpecificationDescription("Filters orders within a date range (inclusive)")]
public class OrderDateRangeSpecification : DateRangeSpecification<Order>
{
    public OrderDateRangeSpecification(DateTime? startDate = null, DateTime? endDate = null)
        : base(startDate, endDate, o => o.OrderDate) { }
}

[SpecificationFor<OrderLine>]
[SpecificationDescription("Filters order lines by product SKU (exact match)")]
public class OrderLineBySkuSpecification : ByStringSpecification<OrderLine>
{
    [SpecificationParameter("The product SKU to match", Required = true)]
    public string ProductSku => Value;

    public OrderLineBySkuSpecification(string productSku) : base(productSku, l => l.ProductSku) { }
}

// ============================================================================
// STEP 2 — DI registration (Program.cs)
// ============================================================================

public static class ReportingSetup
{
    public static IServiceCollection AddOrderReporting(this IServiceCollection services)
    {
        // Registers ISpecificationFactory<T> per entity, IEntitySchemaService,
        // ISpecificationMetadataService, and DataIntelligenceContext.
        services.AddDataIntelligence(di => di
            .AddEntity<Order>()
            .AddEntity<OrderLine>()
            .FromAssemblyOf<OrderByStatusSpecification>());

        services.AddScoped<IOrderReportingService, OrderReportingService>();
        return services;
    }
}

// ============================================================================
// STEP 3 — Reporting service
//   DB-level root filtering (QueryExecutor) + in-memory child filtering
//   (CascadeFilter), mapped to DTOs. Queries run AsNoTracking by default.
// ============================================================================

public interface IOrderReportingService
{
    Task<List<OrderDto>> SearchAsync(
        IEnumerable<SpecificationGroup>? orderGroups,
        IEnumerable<SpecificationGroup>? lineGroups,
        CancellationToken ct);
}

public class OrderReportingService : IOrderReportingService
{
    private readonly AppDbContext _context;
    private readonly ISpecificationFactory<Order> _orderFactory;
    private readonly ISpecificationFactory<OrderLine> _lineFactory;

    private static readonly ChildCollectionAccessor<Order, OrderLine> LinesAccessor = new()
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

    public async Task<List<OrderDto>> SearchAsync(
        IEnumerable<SpecificationGroup>? orderGroups,
        IEnumerable<SpecificationGroup>? lineGroups,
        CancellationToken ct)
    {
        IQueryable<Order> baseQuery = _context.Orders;

        // Load children only when line filters were requested
        if (lineGroups?.Any() == true)
            baseQuery = baseQuery.Include(o => o.Lines);

        // Root filters run as SQL WHERE; AsNoTracking is applied automatically
        var orders = await QueryExecutor.ExecuteAsync(baseQuery, orderGroups, _orderFactory, ct);

        // Child filters run in-memory; parents with no matching lines are removed
        orders = CascadeFilter.Apply(orders, lineGroups, _lineFactory, LinesAccessor);

        return orders.Select(OrderDto.From).ToList();
    }
}

public record OrderDto(Guid Id, string OrderNumber, string Status, decimal Total, List<OrderLineDto> Lines)
{
    public static OrderDto From(Order o) => new(
        o.Id, o.OrderNumber, o.Status.ToString(), o.Total,
        o.Lines.Select(l => new OrderLineDto(l.Id, l.ProductSku, l.Quantity)).ToList());
}

public record OrderLineDto(Guid Id, string ProductSku, int Quantity);

// ============================================================================
// STEP 4 — Search endpoint
//   Clients POST SpecificationGroup JSON; flat or nested parameters both work:
//   { "orderGroups": [ { "operator": "And", "specifications": [
//       { "name": "OrderByStatus", "status": "Pending" } ] } ] }
// ============================================================================

[ApiController]
[Route("api/reports")]
public class OrderReportsController : ControllerBase
{
    private readonly IOrderReportingService _reporting;
    private readonly ISpecificationMetadataService _metadata;
    private readonly IEntitySchemaService _schema;

    public OrderReportsController(
        IOrderReportingService reporting,
        ISpecificationMetadataService metadata,
        IEntitySchemaService schema)
    {
        _reporting = reporting;
        _metadata = metadata;
        _schema = schema;
    }

    [HttpPost("orders/search")]
    public async Task<IActionResult> Search([FromBody] OrderSearchRequest request, CancellationToken ct)
    {
        try
        {
            var results = await _reporting.SearchAsync(request.OrderGroups, request.LineGroups, ct);
            return Ok(results);
        }
        catch (ArgumentException ex)
        {
            // Unknown specification / column / parameter — message lists valid options
            return BadRequest(new { error = ex.Message });
        }
    }

    // ========================================================================
    // STEP 5 — Metadata discovery: lets clients (and LLMs) discover available
    // filters and columns at runtime instead of hardcoding them.
    // ========================================================================

    [HttpGet("specifications")]
    public IActionResult AllSpecifications() => Ok(_metadata.GetAll());

    [HttpGet("specifications/{entityType}")]
    public IActionResult SpecificationsFor(string entityType) => Ok(_metadata.GetForEntity(entityType));

    [HttpGet("schema/{entityType}")]
    public IActionResult SchemaFor(string entityType)
    {
        var schema = _schema.GetSchema(entityType);
        return schema is null ? NotFound() : Ok(schema);
    }
}

public class OrderSearchRequest
{
    public List<SpecificationGroup>? OrderGroups { get; set; }
    public List<SpecificationGroup>? LineGroups { get; set; }
}
