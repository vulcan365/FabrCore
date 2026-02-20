using Fabr.Host.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fabr.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrapi/[controller]")]
    public class FileController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly FileStorageSettings _settings;
        private readonly ILogger<FileController> _logger;

        public FileController(
            IFileStorageService fileStorageService,
            IOptions<FileStorageSettings> settings,
            ILogger<FileController> logger)
        {
            _fileStorageService = fileStorageService;
            _settings = settings.Value;
            _logger = logger;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile(IFormFile file, [FromQuery] int? ttlSeconds = null)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file provided or file is empty.");
            }

            try
            {
                var fileExtension = Path.GetExtension(file.FileName);
                string fileId;

                using (var stream = file.OpenReadStream())
                {
                    fileId = await _fileStorageService.SaveFileAsync(stream, fileExtension);
                }

                var ttl = ttlSeconds ?? _settings.DefaultTtlSeconds;
                var expiresAt = DateTime.UtcNow.AddSeconds(ttl);
                _fileStorageService.TrackFile(fileId, file.FileName, expiresAt);

                _logger.LogInformation($"File uploaded: {fileId}{fileExtension} ({file.FileName}), TTL: {ttl} seconds, Expires at: {expiresAt}");

                return Ok(fileId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file");
                return StatusCode(500, "Error uploading file");
            }
        }

        [HttpGet("{fileId}")]
        public async Task<IActionResult> GetFile(string fileId)
        {
            try
            {
                var (fileStream, contentType) = await _fileStorageService.GetFileAsync(fileId);

                if (fileStream == null || contentType == null)
                {
                    return NotFound($"File with ID {fileId} not found.");
                }

                return File(fileStream, contentType, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving file: {fileId}");
                return StatusCode(500, "Error retrieving file");
            }
        }

        [HttpGet("{fileId}/info")]
        public async Task<IActionResult> GetFileInfo(string fileId)
        {
            try
            {
                var metadata = await _fileStorageService.GetFileMetadataAsync(fileId);

                if (metadata == null)
                {
                    return NotFound($"File with ID {fileId} not found.");
                }

                return Ok(metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving file metadata: {fileId}");
                return StatusCode(500, "Error retrieving file metadata");
            }
        }
    }
}
