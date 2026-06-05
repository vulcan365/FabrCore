using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Route("fabrcoreapi/[controller]")]
public class StorageController : ControllerBase
{
    private readonly IUserScopedFabrCoreStorageProvider _storageProvider;
    private readonly ILogger<StorageController> _logger;

    public StorageController(
        IUserScopedFabrCoreStorageProvider storageProvider,
        ILogger<StorageController> logger)
    {
        _storageProvider = storageProvider;
        _logger = logger;
    }

    [HttpGet("{container}/{*entityKey}")]
    public async Task<IActionResult> GetStorageEntity(
        [FromHeader(Name = "x-user-handle")] string userHandle,
        [FromRoute] string container,
        [FromRoute] string entityKey,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAddress(userHandle, container, entityKey, out var validationResult))
            return validationResult;

        try
        {
            var value = await _storageProvider.GetAsync<JsonElement>(userHandle, container, entityKey, cancellationToken);
            if (value.ValueKind == JsonValueKind.Undefined)
                return NotFound();

            return Ok(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting storage entity {UserHandle}/{Container}/{EntityKey}", userHandle, container, entityKey);
            return StatusCode(500, "Error getting storage entity");
        }
    }

    [HttpPut("{container}/{*entityKey}")]
    public async Task<IActionResult> UpsertStorageEntity(
        [FromHeader(Name = "x-user-handle")] string userHandle,
        [FromRoute] string container,
        [FromRoute] string entityKey,
        [FromBody] JsonElement value,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAddress(userHandle, container, entityKey, out var validationResult))
            return validationResult;

        if (value.ValueKind == JsonValueKind.Undefined)
            return BadRequest("Request body must contain a JSON value.");

        try
        {
            await _storageProvider.UpsertAsync(userHandle, container, entityKey, value, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting storage entity {UserHandle}/{Container}/{EntityKey}", userHandle, container, entityKey);
            return StatusCode(500, "Error upserting storage entity");
        }
    }

    [HttpDelete("{container}/{*entityKey}")]
    public async Task<IActionResult> DeleteStorageEntity(
        [FromHeader(Name = "x-user-handle")] string userHandle,
        [FromRoute] string container,
        [FromRoute] string entityKey,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAddress(userHandle, container, entityKey, out var validationResult))
            return validationResult;

        try
        {
            var deleted = await _storageProvider.DeleteAsync(userHandle, container, entityKey, cancellationToken);
            return deleted ? NoContent() : NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting storage entity {UserHandle}/{Container}/{EntityKey}", userHandle, container, entityKey);
            return StatusCode(500, "Error deleting storage entity");
        }
    }

    private static bool TryValidateAddress(
        string userHandle,
        string container,
        string entityKey,
        out IActionResult result)
    {
        if (string.IsNullOrWhiteSpace(userHandle))
        {
            result = new BadRequestObjectResult("x-user-handle header is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(container))
        {
            result = new BadRequestObjectResult("Container is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(entityKey))
        {
            result = new BadRequestObjectResult("Entity key is required.");
            return false;
        }

        result = new EmptyResult();
        return true;
    }
}
