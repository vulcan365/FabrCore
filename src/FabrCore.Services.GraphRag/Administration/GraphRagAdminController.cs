using System.Diagnostics;
using System.Text.Json;
using FabrCore.Core.Acl;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Administration;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "FabrCoreAdmin")]
[Route("fabrcoreapi/graphrag/admin/v1")]
public sealed class GraphRagAdminController(
    IServiceProvider services,
    IKnowledgeScopeService scopeService,
    IGraphRagAuditLog graphRagAudit,
    ILogger<GraphRagAdminController> logger) : ControllerBase
{
    private const long MaxUploadBytes = 100L * 1024 * 1024;
    private static readonly AclAction ReadAction = new("graphrag", "read");
    private static readonly AclAction ManageAction = new("graphrag", "manage");
    private static readonly string[] Features =
    [
        "dashboard", "scopes", "documents", "entities", "relationships",
        "taxonomy", "graph", "search", "metrics", "maintenance", "upload"
    ];

    private IGraphRagAdminClient Client
        => services.GetRequiredKeyedService<IGraphRagAdminClient>(GraphRagAdminClientKeys.Local);

    [HttpGet("capabilities")]
    public Task<IActionResult> GetCapabilities([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            var capability = await Client.GetCapabilityAsync(ct);
            await Client.GetDashboardStatsAsync(ct);
            return Ok(new GraphRagAdminCapability
            {
                Availability = capability.Availability,
                ApiVersion = GraphRagAdminCapability.CurrentApiVersion,
                Features = Features,
                Message = capability.Message
            });
        });

    [HttpGet("dashboard")]
    public Task<IActionResult> GetDashboard([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.GetDashboardStatsAsync(ct)));

    [HttpGet("scopes")]
    public Task<IActionResult> ListScopes([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListScopesAsync(ct)));

    [HttpGet("scopes/{scopeKey}")]
    public Task<IActionResult> GetScope([FromHeader(Name = "x-user-handle")] string? principal, string scopeKey, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            var scope = await Client.GetScopeAsync(scopeKey, ct);
            return scope is null ? NotFoundProblem("The requested GraphRAG scope was not found.") : Ok(scope);
        });

    [HttpPost("scopes/{scopeKey}")]
    public Task<IActionResult> CreateScope([FromHeader(Name = "x-user-handle")] string? principal, string scopeKey, GraphRagScopeWriteRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.CreateScopeAsync(scopeKey, request.Description, request.DefaultPriority, request.Metadata, ct)),
            new MutationAudit("AdminScopeCreated", "Scope", scopeKey, scopeKey));

    [HttpPut("scopes/{scopeKey}")]
    public Task<IActionResult> UpdateScope([FromHeader(Name = "x-user-handle")] string? principal, string scopeKey, GraphRagScopeWriteRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () =>
        {
            await RequireScopeAsync(scopeKey, ct);
            return Ok(await Client.UpdateScopeAsync(scopeKey, request.Description, request.DefaultPriority, request.Metadata, ct));
        }, new MutationAudit("AdminScopeUpdated", "Scope", scopeKey, scopeKey));

    [HttpGet("documents")]
    public Task<IActionResult> ListDocuments([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            ValidatePage(page, pageSize);
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.ListDocumentsAsync(scope, page, pageSize, ct));
        });

    [HttpGet("documents/count")]
    public Task<IActionResult> CountDocuments([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.CountDocumentsAsync(scope, ct));
        });

    [HttpGet("documents/{documentId:guid}")]
    public Task<IActionResult> GetDocument([FromHeader(Name = "x-user-handle")] string? principal, Guid documentId, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            var document = await Client.GetDocumentAsync(documentId, ct);
            return document is null ? NotFoundProblem("The requested GraphRAG document was not found.") : Ok(document);
        });

    [HttpPost("documents/upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public Task<IActionResult> UploadDocument(
        [FromHeader(Name = "x-user-handle")] string? principal,
        [FromForm] IFormFile? file,
        [FromForm] string? scopeKey,
        [FromForm] string? extractionInstructions,
        CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () =>
        {
            if (file is null || file.Length == 0) throw new ArgumentException("A non-empty document file is required.");
            if (file.Length > MaxUploadBytes) throw new ArgumentException($"The document exceeds the {MaxUploadBytes} byte upload limit.");
            await RequireScopeAsync(scopeKey!, ct);
            await using var stream = file.OpenReadStream();
            var document = await Client.IngestDocumentAsync(new GraphRagDocumentUpload(file.FileName, file.ContentType, stream, scopeKey!, extractionInstructions), ct);
            return Ok(document);
        }, new MutationAudit("AdminDocumentIngested", "Document", null, scopeKey));

    [HttpDelete("documents/{documentId:guid}")]
    public Task<IActionResult> DeleteDocument([FromHeader(Name = "x-user-handle")] string? principal, Guid documentId, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () =>
        {
            var document = await Client.GetDocumentAsync(documentId, ct);
            if (document is null) return NotFoundProblem("The requested GraphRAG document was not found.");
            await Client.DeleteDocumentAsync(documentId, ct);
            return NoContent();
        }, new MutationAudit("AdminDocumentDeleted", "Document", documentId.ToString("D"), null));

    [HttpGet("entities")]
    public Task<IActionResult> ListEntities([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, [FromQuery] string? entityType, [FromQuery(Name = "search")] string? searchTerm, int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            ValidatePage(page, pageSize);
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.ListEntitiesAsync(scope, entityType, searchTerm, page, pageSize, ct));
        });

    [HttpGet("entities/count")]
    public Task<IActionResult> CountEntities([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, [FromQuery] string? entityType, [FromQuery(Name = "search")] string? searchTerm, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.CountEntitiesAsync(scope, entityType, searchTerm, ct));
        });

    [HttpGet("entities/{entityId:guid}")]
    public Task<IActionResult> GetEntity([FromHeader(Name = "x-user-handle")] string? principal, Guid entityId, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            var entity = await Client.GetEntityAsync(entityId, ct);
            return entity is null ? NotFoundProblem("The requested GraphRAG entity was not found.") : Ok(entity);
        });

    [HttpPut("entities/{entityId:guid}")]
    public Task<IActionResult> UpdateEntity([FromHeader(Name = "x-user-handle")] string? principal, Guid entityId, GraphRagEntityUpdateRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.UpdateEntityAsync(entityId, request.Description, request.Content, request.Metadata, ct)),
            new MutationAudit("AdminEntityUpdated", "Entity", entityId.ToString("D"), null));

    [HttpDelete("entities/{entityId:guid}")]
    public Task<IActionResult> DeleteEntity([FromHeader(Name = "x-user-handle")] string? principal, Guid entityId, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => { await Client.DeleteEntityAsync(entityId, ct); return NoContent(); },
            new MutationAudit("AdminEntityDeleted", "Entity", entityId.ToString("D"), null));

    [HttpGet("entities/{entityId:guid}/chunks")]
    public Task<IActionResult> ListChunks([FromHeader(Name = "x-user-handle")] string? principal, Guid entityId, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListChunksForEntityAsync(entityId, ct)));

    [HttpGet("entity-types")]
    public Task<IActionResult> ListEntityTypes([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListEntityTypesAsync(ct)));

    [HttpGet("relationships")]
    public Task<IActionResult> ListRelationships([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, [FromQuery] string? entityName, [FromQuery] string? relationshipType, int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            ValidatePage(page, pageSize);
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.ListRelationshipsAsync(scope, entityName, relationshipType, page, pageSize, ct));
        });

    [HttpGet("relationships/count")]
    public Task<IActionResult> CountRelationships([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, [FromQuery] string? entityName, [FromQuery] string? relationshipType, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            await RequireOptionalScopeAsync(scope, ct);
            return Ok(await Client.CountRelationshipsAsync(scope, entityName, relationshipType, ct));
        });

    [HttpDelete("relationships")]
    public Task<IActionResult> DeleteRelationship([FromHeader(Name = "x-user-handle")] string? principal, GraphRagRelationshipDeleteRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () =>
        {
            await RequireScopeAsync(request.ScopeKey, ct);
            await Client.DeleteRelationshipAsync(request.FromEntityName, request.FromEntityType, request.ToEntityName, request.ToEntityType, request.RelationshipType, request.ScopeKey, ct);
            return NoContent();
        }, new MutationAudit("AdminRelationshipDeleted", "Relationship", request.RelationshipType, request.ScopeKey));

    [HttpGet("relationship-types")]
    public Task<IActionResult> ListRelationshipTypes([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListRelationshipTypesAsync(ct)));

    [HttpGet("domains")]
    public Task<IActionResult> ListDomains([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListDomainsAsync(ct)));

    [HttpPost("domains")]
    public Task<IActionResult> CreateDomain([FromHeader(Name = "x-user-handle")] string? principal, GraphRagDomainCreateRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.CreateDomainAsync(request.Name, request.Description, request.PriorityWeight, request.Metadata, ct)),
            new MutationAudit("AdminDomainCreated", "Domain", request.Name, null));

    [HttpPut("domains/{domainId:guid}")]
    public Task<IActionResult> UpdateDomain([FromHeader(Name = "x-user-handle")] string? principal, Guid domainId, GraphRagDomainUpdateRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.UpdateDomainAsync(domainId, request.Description, request.PriorityWeight, request.Metadata, ct)),
            new MutationAudit("AdminDomainUpdated", "Domain", domainId.ToString("D"), null));

    [HttpDelete("domains/{domainId:guid}")]
    public Task<IActionResult> DeleteDomain([FromHeader(Name = "x-user-handle")] string? principal, Guid domainId, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => { await Client.DeleteDomainAsync(domainId, ct); return NoContent(); },
            new MutationAudit("AdminDomainDeleted", "Domain", domainId.ToString("D"), null));

    [HttpGet("categories")]
    public Task<IActionResult> ListCategories([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "domain")] string? domain, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.ListCategoriesAsync(domain, ct)));

    [HttpPost("categories")]
    public Task<IActionResult> CreateCategory([FromHeader(Name = "x-user-handle")] string? principal, GraphRagCategoryCreateRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.CreateCategoryAsync(request.Name, request.DomainName, request.Description, request.Metadata, ct)),
            new MutationAudit("AdminCategoryCreated", "Category", request.Name, null));

    [HttpPut("categories/{categoryId:guid}")]
    public Task<IActionResult> UpdateCategory([FromHeader(Name = "x-user-handle")] string? principal, Guid categoryId, GraphRagCategoryUpdateRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => Ok(await Client.UpdateCategoryAsync(categoryId, request.Description, request.Metadata, ct)),
            new MutationAudit("AdminCategoryUpdated", "Category", categoryId.ToString("D"), null));

    [HttpDelete("categories/{categoryId:guid}")]
    public Task<IActionResult> DeleteCategory([FromHeader(Name = "x-user-handle")] string? principal, Guid categoryId, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () => { await Client.DeleteCategoryAsync(categoryId, ct); return NoContent(); },
            new MutationAudit("AdminCategoryDeleted", "Category", categoryId.ToString("D"), null));

    [HttpGet("graph")]
    public Task<IActionResult> GetGraph([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery(Name = "scope")] string? scope, int maxNodes = 200, CancellationToken ct = default)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            await RequireOptionalScopeAsync(scope, ct);
            if (maxNodes is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxNodes), "maxNodes must be between 1 and 1000.");
            return Ok(await Client.GetGraphDataAsync(scope, maxNodes, ct));
        });

    [HttpPost("search")]
    public Task<IActionResult> Search([FromHeader(Name = "x-user-handle")] string? principal, GraphRagSearchRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            if (request.Scopes.Count == 0) throw new ArgumentException("At least one GraphRAG scope is required.");
            foreach (var scope in request.Scopes.Distinct(StringComparer.OrdinalIgnoreCase)) await RequireScopeAsync(scope, ct);
            return Ok(await Client.SearchAsync(request.Query, request.Scopes, request.SearchType, request.Limit, request.EntityTypeFilter, request.DomainFilter, ct));
        });

    [HttpGet("maintenance/orphans")]
    public Task<IActionResult> GetOrphans([FromHeader(Name = "x-user-handle")] string? principal, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.GetOrphanTaxonomyAsync(ct)));

    [HttpPost("maintenance/purge")]
    public Task<IActionResult> PurgeOrphans([FromHeader(Name = "x-user-handle")] string? principal, GraphRagPurgeTaxonomyRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ManageAction, async () =>
        {
            await Client.PurgeOrphanTaxonomyAsync(request.DomainIds, request.CategoryIds, ct);
            return NoContent();
        }, new MutationAudit("AdminOrphanTaxonomyPurged", "Taxonomy", null, null));

    [HttpGet("metrics")]
    public Task<IActionResult> GetMetrics([FromHeader(Name = "x-user-handle")] string? principal, [FromQuery] string? scope, [FromQuery] DateTime? since, int topN = 25, CancellationToken ct = default)
        => ExecuteAsync(principal, ReadAction, async () =>
        {
            await RequireOptionalScopeAsync(scope, ct);
            if (topN is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(topN), "topN must be between 1 and 200.");
            return Ok(await Client.GetMetricsSummaryAsync(scope, since, topN, ct));
        });

    [HttpPost("metrics/documents")]
    public Task<IActionResult> GetDocumentMetrics([FromHeader(Name = "x-user-handle")] string? principal, GraphRagDocumentIdsRequest request, CancellationToken ct)
        => ExecuteAsync(principal, ReadAction, async () => Ok(await Client.GetDocumentTokenSummariesAsync(request.DocumentIds, ct)));

    private async Task<IActionResult> ExecuteAsync(
        string? principal,
        AclAction action,
        Func<Task<IActionResult>> operation,
        MutationAudit? mutation = null)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return ProblemResult(StatusCodes.Status401Unauthorized, "Surface principal required", "The x-user-handle header is required.", GraphRagAdminProblemCodes.Unauthorized);
        }

        var enforcer = services.GetService<AclEnforcer>();
        if (enforcer is null)
        {
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "Authorization unavailable", "The FabrCore ACL enforcer is not available on this host.", GraphRagAdminProblemCodes.Unavailable);
        }

        try
        {
            var subject = new AclSubjectContext(principal.Trim(), null);
            enforcer.Authorize(in subject, action, "*:*");
        }
        catch (AclDeniedException)
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "GraphRAG administration denied", "The current principal is not authorized for this GraphRAG administration operation.", GraphRagAdminProblemCodes.Unauthorized);
        }

        try
        {
            var result = await operation();
            if (mutation is not null && IsSuccessful(result)) await RecordMutationAsync(principal, mutation, succeeded: true, null);
            return result;
        }
        catch (ConcurrentIngestionException ex)
        {
            if (mutation is not null) await RecordMutationAsync(principal, mutation, false, ex.Message);
            return ProblemResult(StatusCodes.Status409Conflict, "GraphRAG operation conflict", ex.Message, GraphRagAdminProblemCodes.Conflict);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "GraphRAG administration dependencies are unavailable for principal {Principal}.", principal);
            if (mutation is not null) await RecordMutationAsync(principal, mutation, false, "Operation unavailable.");
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "GraphRAG administration unavailable", "The GraphRAG service is not fully configured or available.", GraphRagAdminProblemCodes.Unavailable);
        }
        catch (ArgumentException ex)
        {
            if (mutation is not null) await RecordMutationAsync(principal, mutation, false, ex.Message);
            return ProblemResult(StatusCodes.Status400BadRequest, "Invalid GraphRAG request", ex.Message, GraphRagAdminProblemCodes.ValidationFailed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GraphRAG administration operation failed for principal {Principal}.", principal);
            if (mutation is not null) await RecordMutationAsync(principal, mutation, false, "Operation failed.");
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "GraphRAG administration unavailable", "The GraphRAG service could not complete the operation.", GraphRagAdminProblemCodes.Unavailable);
        }
    }

    private async Task RequireScopeAsync(string scopeKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) throw new ArgumentException("A GraphRAG scope is required.", nameof(scopeKey));
        if (!await scopeService.ScopeExistsAsync(scopeKey.Trim(), ct)) throw new ArgumentException($"GraphRAG scope '{scopeKey}' is not registered.", nameof(scopeKey));
    }

    private Task RequireOptionalScopeAsync(string? scopeKey, CancellationToken ct)
        => string.IsNullOrWhiteSpace(scopeKey) ? Task.CompletedTask : RequireScopeAsync(scopeKey, ct);

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "page must be at least 1.");
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be between 1 and 200.");
    }

    private static bool IsSuccessful(IActionResult result)
        => result switch
        {
            ObjectResult { StatusCode: >= 400 } => false,
            StatusCodeResult { StatusCode: >= 400 } => false,
            _ => true
        };

    private async Task RecordMutationAsync(string principal, MutationAudit mutation, bool succeeded, string? error)
    {
        await graphRagAudit.RecordAsync(new GraphRagAuditEntry
        {
            ActionType = mutation.ActionType,
            Severity = succeeded ? AuditSeverity.Info : AuditSeverity.Error,
            ActorKind = "Principal",
            ActorId = principal,
            SubjectKind = mutation.SubjectKind,
            SubjectId = mutation.SubjectId,
            ScopeKey = mutation.ScopeKey,
            Summary = succeeded ? $"{mutation.ActionType} succeeded." : $"{mutation.ActionType} failed.",
            Payload = JsonSerializer.Serialize(new { succeeded, error, traceId = Activity.Current?.TraceId.ToString() })
        });
    }

    private ObjectResult NotFoundProblem(string detail)
        => ProblemResult(StatusCodes.Status404NotFound, "GraphRAG resource not found", detail, GraphRagAdminProblemCodes.NotFound);

    private ObjectResult ProblemResult(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = HttpContext?.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? HttpContext?.TraceIdentifier;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private sealed record MutationAudit(string ActionType, string SubjectKind, string? SubjectId, string? ScopeKey);
}
