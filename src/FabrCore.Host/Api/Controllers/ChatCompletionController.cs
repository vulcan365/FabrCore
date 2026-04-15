using FabrCore.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class ChatCompletionController : Controller
    {
        private readonly ILogger<ChatCompletionController> logger;
        private readonly IFabrCoreChatClientService chatClientService;

        public ChatCompletionController(ILogger<ChatCompletionController> logger, IFabrCoreChatClientService chatClientService)
        {
            this.logger = logger;
            this.chatClientService = chatClientService;
        }

        [HttpPost]
        public async Task<IActionResult> Complete([FromBody] ChatCompletionRequest request)
        {
            if (request.Messages == null || request.Messages.Count == 0)
            {
                return BadRequest("Messages list must not be empty.");
            }

            for (int i = 0; i < request.Messages.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(request.Messages[i].Content))
                    return BadRequest($"Message at index {i} has empty content.");
            }

            try
            {
                var modelName = request.Options?.Model ?? "default";
                var chatClient = await chatClientService.GetChatClient(modelName);

                var messages = request.Messages.Select(m => new ChatMessage(
                    MapRole(m.Role),
                    m.Content
                )).ToList();

                var chatOptions = BuildChatOptions(request.Options);

                var response = await chatClient.GetResponseAsync(messages, chatOptions);

                return Ok(new ChatCompletionResponse
                {
                    Text = response.Text ?? string.Empty,
                    Model = response.ModelId ?? modelName,
                    Usage = new ChatCompletionUsage
                    {
                        InputTokens = (int)(response.Usage?.InputTokenCount ?? 0),
                        OutputTokens = (int)(response.Usage?.OutputTokenCount ?? 0)
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error completing chat for model {Model}", request.Options?.Model ?? "default");
                return StatusCode(500, "Failed to complete chat request.");
            }
        }

        private static ChatRole MapRole(string role)
        {
            return role?.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.User
            };
        }

        private static ChatOptions? BuildChatOptions(ChatCompletionOptions? options)
        {
            if (options == null)
                return null;

            var chatOptions = new ChatOptions();

            if (options.MaxOutputTokens.HasValue)
                chatOptions.MaxOutputTokens = options.MaxOutputTokens.Value;

            if (options.Temperature.HasValue)
                chatOptions.Temperature = options.Temperature.Value;

            if (options.TopP.HasValue)
                chatOptions.TopP = options.TopP.Value;

            if (options.TopK.HasValue)
                chatOptions.TopK = options.TopK.Value;

            if (options.FrequencyPenalty.HasValue)
                chatOptions.FrequencyPenalty = options.FrequencyPenalty.Value;

            if (options.PresencePenalty.HasValue)
                chatOptions.PresencePenalty = options.PresencePenalty.Value;

            if (options.StopSequences is { Count: > 0 })
                chatOptions.StopSequences = options.StopSequences;

            return chatOptions;
        }
    }

    public class ChatCompletionMessageRequest
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = string.Empty;
    }

    public class ChatCompletionOptions
    {
        public string Model { get; set; } = "default";
        public int? MaxOutputTokens { get; set; }
        public float? Temperature { get; set; }
        public float? TopP { get; set; }
        public int? TopK { get; set; }
        public List<string>? StopSequences { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
    }

    public class ChatCompletionRequest
    {
        public List<ChatCompletionMessageRequest> Messages { get; set; } = new();
        public ChatCompletionOptions? Options { get; set; }
    }

    public class ChatCompletionUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    public class ChatCompletionResponse
    {
        public string Text { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public ChatCompletionUsage Usage { get; set; } = new();
    }
}
