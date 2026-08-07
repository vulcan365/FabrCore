using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class ModelConfigController : Controller
    {
        private readonly ILogger<ModelConfigController> logger;
        private readonly IFabrCoreConfigurationStore configurationStore;

        public ModelConfigController(ILogger<ModelConfigController> logger, IFabrCoreConfigurationStore configurationStore)
        {
            this.logger = logger;
            this.configurationStore = configurationStore;
        }

        [HttpGet("model/{name}")]
        public async Task<IActionResult> GetModelConfig(string name)
        {
            try
            {
                var config = await configurationStore.GetConfigurationAsync();
                var modelConfig = config.ModelConfigurations.FirstOrDefault(m => m.Name == name);

                if (modelConfig == null)
                {
                    return NotFound($"Model configuration '{name}' not found.");
                }

                return Ok(new
                {
                    modelConfig.Name,
                    modelConfig.Provider,
                    modelConfig.Uri,
                    modelConfig.Model,
                    modelConfig.ApiKeyAlias,
                    modelConfig.TimeoutSeconds,
                    modelConfig.MaxOutputTokens,
                    modelConfig.ReasoningEffort,
                    modelConfig.ContextWindowTokens,
                    modelConfig.ContextCompactionEnabled,
                    modelConfig.ContextEvictThreshold,
                    modelConfig.ContextTruncateThreshold,
                    modelConfig.CompactionEnabled,
                    modelConfig.CompactionKeepLastN,
                    modelConfig.CompactionThreshold,
                    modelConfig.CompactionStaleAfterMinutes,
                    modelConfig.PerTurnMaxInputTokens,
                    modelConfig.MaxPromptInputTokens,
                    modelConfig.RunawayBudgetBehavior
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting model configuration for {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("apikey/{alias}")]
        public async Task<IActionResult> GetApiKey(string alias)
        {
            try
            {
                var config = await configurationStore.GetConfigurationAsync();
                var apiKey = config.ApiKeys.FirstOrDefault(k => k.Alias == alias);

                if (apiKey == null)
                {
                    return NotFound($"API key with alias '{alias}' not found.");
                }

                return Ok(new { Value = apiKey.Value });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting API key for alias {Alias}", alias);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
