using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Security;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
namespace FabrCore.Host.Api.Controllers;
[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/observability/evidence")]
public sealed class EvidenceAdministrationController(IVerifiableExecutionStore evidence, IVerifiableExecutionVerifier verifier,
    IUserScopedFabrCoreStorageProvider storage) : ControllerBase
{
    private string ActorKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Request.Headers[RemoteAdminController.ActorHeader].FirstOrDefault() ?? User.Identity!.Name!)));
    [HttpGet("traces")]
    public async Task<IActionResult> List(string? agentHandle = null, string? after = null, int limit = 100, CancellationToken cancellationToken = default) =>
        evidence is IVerifiableExecutionQueryProvider query ? Ok(await query.ListTracesAsync(agentHandle, after, limit, cancellationToken)) : StatusCode(501, new { error = "Trace discovery is unavailable for this provider." });
    [HttpPost("{traceId}/exports")]
    public async Task<IActionResult> CreateExport(string traceId, CancellationToken cancellationToken)
    {
        var bundle = await evidence.GetBundleAsync(traceId, cancellationToken);
        if (bundle.Records.Count == 0) return NotFound();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonSerializerOptions.Web);
        var revision = Convert.ToHexString(SHA256.HashData(bytes));
        var id = Guid.NewGuid().ToString("N");
        var maximumBody = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<FabrCore.Host.Configuration.RemoteAdministrationOptions>>().Value.MaxBodyBytes;
        // Leave room for base64 expansion and the JSON envelope even at the 1 KiB transport minimum.
        var chunkBytes = Math.Clamp((maximumBody - 512) * 3 / 4, 1, 262144);
        var chunks = (bytes.Length + chunkBytes - 1) / chunkBytes;
        for (var i = 0; i < chunks; i++) await storage.UpsertAsync("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}.{i}",
            bytes.Skip(i * chunkBytes).Take(chunkBytes).ToArray(), cancellationToken);
        var manifest = new EvidenceExportManifest { Id = id, TraceId = traceId, Revision = revision, Chunks = chunks,
            Bytes = bytes.Length, CreatedAt = DateTimeOffset.UtcNow, Verification = await verifier.VerifyAsync(bundle, cancellationToken),
            DataScope = evidence is FabrCore.Host.Database.SqlVerifiableExecutionStore ? "cluster" : "silo" };
        await storage.UpsertAsync("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}", manifest, cancellationToken);
        return Ok(manifest);
    }
    [HttpGet("exports/{id}/{chunk:int}")]
    public async Task<IActionResult> Chunk(string id, int chunk, CancellationToken cancellationToken)
    {
        var manifest = await storage.GetAsync<EvidenceExportManifest>("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}", cancellationToken);
        if (manifest is null || chunk < 0 || chunk >= manifest.Chunks) return NotFound();
        Response.Headers.ETag = $"\"{manifest.Revision}\"";
        return Ok(new { manifest.Revision, chunk, data = await storage.GetAsync<byte[]>("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}.{chunk}", cancellationToken) });
    }
    [HttpDelete("exports/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var manifest = await storage.GetAsync<EvidenceExportManifest>("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}", cancellationToken);
        if (manifest is null) return NotFound();
        for (var i = 0; i < manifest.Chunks; i++) await storage.DeleteAsync("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}.{i}", cancellationToken);
        await storage.DeleteAsync("system", "fabrcore.evidence-exports", $"{ActorKey}.{id}", cancellationToken);
        return NoContent();
    }
}
public sealed class EvidenceExportManifest
{
    public string Id { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string Revision { get; set; } = "";
    public int Chunks { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public VerifiableExecutionVerificationResult? Verification { get; set; }
    public string DataScope { get; set; } = "silo";
    public string CoverageNotice { get; set; } = "Verification covers exported records. It does not establish that every cluster execution record was captured.";
}
