using FabrCore.Core.VerifiableExecution;
using Microsoft.AspNetCore.Mvc;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Route("fabrcoreapi/monitor/verifiable-execution")]
public sealed class VerifiableExecutionController : ControllerBase
{
    private readonly IVerifiableExecutionStore _store;
    private readonly IVerifiableExecutionVerifier _verifier;
    private readonly IVerifiableExecutionContext _context;

    public VerifiableExecutionController(
        IVerifiableExecutionStore store,
        IVerifiableExecutionVerifier verifier,
        IVerifiableExecutionContext context)
    {
        _store = store;
        _verifier = verifier;
        _context = context;
    }

    [HttpGet("operations/{traceId}")]
    public async Task<ActionResult<VerifiableExecutionBundle>> GetOperation(string traceId, CancellationToken cancellationToken)
    {
        var bundle = await _store.GetBundleAsync(traceId, cancellationToken);
        return Ok(bundle);
    }

    [HttpGet("operations/{traceId}/bundle")]
    public async Task<ActionResult<VerifiableExecutionBundle>> GetBundle(string traceId, CancellationToken cancellationToken)
    {
        var bundle = await _store.GetBundleAsync(traceId, cancellationToken);
        return Ok(bundle);
    }

    [HttpGet("operations/{traceId}/verify")]
    public async Task<ActionResult<VerifiableExecutionVerificationResult>> VerifyGet(string traceId, CancellationToken cancellationToken)
    {
        var result = await _context.VerifyAsync(traceId, cancellationToken);
        return Ok(result);
    }

    [HttpPost("operations/{traceId}/verify")]
    public async Task<ActionResult<VerifiableExecutionVerificationResult>> VerifyPost(string traceId, CancellationToken cancellationToken)
    {
        var bundle = await _store.GetBundleAsync(traceId, cancellationToken);
        var result = await _verifier.VerifyAsync(bundle, cancellationToken);
        return Ok(result);
    }
}
