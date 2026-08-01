using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Route("fabrcoreapi/[controller]")]
public sealed class BlueprintController(IFabrCoreBlueprintService blueprints) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromHeader(Name = "x-user-handle")] string principalId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return BadRequest("x-user-handle header is required.");
        }

        return Ok(await blueprints.ListAsync(principalId, cancellationToken));
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "x-user-handle")] string principalId,
        string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return BadRequest("x-user-handle header is required.");
        }

        var blueprint = await blueprints.GetAsync(principalId, name, cancellationToken);
        return blueprint is null ? NotFound() : Ok(blueprint);
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Put(
        [FromHeader(Name = "x-user-handle")] string principalId,
        string name,
        [FromBody] FabrCoreBlueprint blueprint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return BadRequest("x-user-handle header is required.");
        }

        blueprint.Name = name;
        await blueprints.SaveAsync(principalId, blueprint, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(
        [FromHeader(Name = "x-user-handle")] string principalId,
        string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return BadRequest("x-user-handle header is required.");
        }

        return await blueprints.DeleteAsync(principalId, name, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("{name}/apply")]
    public async Task<IActionResult> ApplyStored(
        [FromHeader(Name = "x-user-handle")] string principalId,
        string name,
        [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return BadRequest("x-user-handle header is required.");
        }

        var blueprint = await blueprints.GetAsync(principalId, name, cancellationToken);
        if (blueprint is null)
        {
            return NotFound();
        }

        return Ok(await blueprints.ApplyAsync(
            principalId, blueprint, detailLevel, cancellationToken));
    }
}
