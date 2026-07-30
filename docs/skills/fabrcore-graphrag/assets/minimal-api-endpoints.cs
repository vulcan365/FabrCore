using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Services;

app.MapGet("/api/graphrag/scopes", async (
    IKnowledgeScopeService scopes,
    CancellationToken ct) =>
{
    return Results.Ok(await scopes.ListScopesAsync(ct));
});

app.MapPost("/api/graphrag/scopes", async (
    CreateGraphRagScopeRequest request,
    IKnowledgeScopeService scopes,
    CancellationToken ct) =>
{
    var scope = await scopes.CreateScopeAsync(
        request.ScopeKey,
        request.Description,
        request.DefaultPriority,
        request.Metadata,
        ct);

    return Results.Created($"/api/graphrag/scopes/{scope.ScopeKey}", scope);
});

app.MapPost("/api/graphrag/documents", async (
    IngestGraphRagDocumentRequest request,
    IKnowledgeIngestionService ingestion,
    CancellationToken ct) =>
{
    var result = await ingestion.IngestDocumentAsync(
        request.FileName,
        request.ScopeKey,
        request.MarkdownContent,
        ct);

    return Results.Ok(result);
});

app.MapPost("/api/graphrag/search/entities", async (
    GraphRagSearchRequest request,
    IKnowledgeSearchService search,
    CancellationToken ct) =>
{
    var graphRequest = new ScopedSearchRequest(
        request.Query,
        request.Scopes,
        request.Limit,
        request.EntityTypeFilter,
        request.DomainFilter);

    var json = await search.SearchEntitiesAsync(graphRequest, ct);
    return Results.Content(json, "application/json");
});

app.MapGet("/api/graphrag/admin/dashboard", async (
    IGraphRagAdminService admin,
    CancellationToken ct) =>
{
    return Results.Ok(await admin.GetDashboardStatsAsync(ct));
});

public sealed record CreateGraphRagScopeRequest(
    string ScopeKey,
    string Description,
    double DefaultPriority = 1.0,
    string? Metadata = null);

public sealed record IngestGraphRagDocumentRequest(
    string FileName,
    string ScopeKey,
    string MarkdownContent);

public sealed record GraphRagSearchRequest(
    string Query,
    IReadOnlyList<string> Scopes,
    int Limit = 10,
    string? EntityTypeFilter = null,
    string? DomainFilter = null);
