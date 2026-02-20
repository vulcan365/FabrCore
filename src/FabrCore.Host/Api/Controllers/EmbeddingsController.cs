using Fabr.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Fabr.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrapi/[controller]")]
    public class EmbeddingsController : Controller
    {
        private readonly ILogger<EmbeddingsController> logger;
        private readonly IEmbeddings embeddings;

        public EmbeddingsController(ILogger<EmbeddingsController> logger, IEmbeddings embeddings)
        {
            this.logger = logger;
            this.embeddings = embeddings;
        }

        [HttpPost]
        public async Task<IActionResult> GetEmbeddings([FromBody] EmbeddingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest("Text is required.");
            }

            try
            {
                var result = await embeddings.GetEmbeddings(request.Text);

                return Ok(new EmbeddingResponse
                {
                    Vector = result.Vector.ToArray(),
                    Dimensions = result.Vector.Length
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generating embeddings for text");
                return StatusCode(500, "Failed to generate embeddings.");
            }
        }
    }

    public class EmbeddingRequest
    {
        public string Text { get; set; } = string.Empty;
    }

    public class EmbeddingResponse
    {
        public float[] Vector { get; set; } = [];
        public int Dimensions { get; set; }
    }
}
