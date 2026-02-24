using FabrCore.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
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

        [HttpPost("batch")]
        public async Task<IActionResult> GetBatchEmbeddings([FromBody] BatchEmbeddingRequest request)
        {
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest("Items list must not be empty.");
            }

            if (request.Items.Count > 2048)
            {
                return BadRequest($"Batch size {request.Items.Count} exceeds maximum of 2048.");
            }

            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                if (string.IsNullOrWhiteSpace(item.Id))
                    return BadRequest($"Item at index {i} has an empty Id.");
                if (string.IsNullOrWhiteSpace(item.Text))
                    return BadRequest($"Item at index {i} (Id='{item.Id}') has empty Text.");
            }

            try
            {
                var texts = request.Items.Select(i => i.Text).ToList();
                var results = await embeddings.GetBatchEmbeddings(texts);

                var response = new BatchEmbeddingResponse
                {
                    Results = request.Items.Zip(results, (item, embedding) => new BatchEmbeddingResultItem
                    {
                        Id = item.Id,
                        Vector = embedding.Vector.ToArray(),
                        Dimensions = embedding.Vector.Length
                    }).ToList()
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generating batch embeddings for {Count} items", request.Items.Count);
                return StatusCode(500, "Failed to generate batch embeddings.");
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

    public class BatchEmbeddingItem
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    public class BatchEmbeddingRequest
    {
        public List<BatchEmbeddingItem> Items { get; set; } = new();
    }

    public class BatchEmbeddingResultItem
    {
        public string Id { get; set; } = string.Empty;
        public float[] Vector { get; set; } = [];
        public int Dimensions { get; set; }
    }

    public class BatchEmbeddingResponse
    {
        public List<BatchEmbeddingResultItem> Results { get; set; } = new();
    }
}
