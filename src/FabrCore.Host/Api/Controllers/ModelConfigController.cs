using FabrCore.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class ModelConfigController : Controller
    {
        private readonly ILogger<ModelConfigController> logger;
        private readonly string configFilePath;

        public ModelConfigController(ILogger<ModelConfigController> logger, IWebHostEnvironment env)
        {
            this.logger = logger;
            this.configFilePath = Path.Combine(env.ContentRootPath, "fabrcore.json");
        }

        [HttpGet("model/{name}")]
        public async Task<IActionResult> GetModelConfig(string name)
        {
            try
            {
                var config = await LoadConfiguration();
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
                    modelConfig.ContextWindowTokens
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
                var config = await LoadConfiguration();
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

        private async Task<FabrCoreConfiguration> LoadConfiguration()
        {
            if (!System.IO.File.Exists(configFilePath))
            {
                logger.LogWarning("Configuration file {Path} not found. Creating default configuration.", configFilePath);
                var defaultConfig = new FabrCoreConfiguration();
                await SaveConfiguration(defaultConfig);
                return defaultConfig;
            }

            var json = await System.IO.File.ReadAllTextAsync(configFilePath);
            return JsonSerializer.Deserialize<FabrCoreConfiguration>(json) ?? new FabrCoreConfiguration();
        }

        private async Task SaveConfiguration(FabrCoreConfiguration config)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            await System.IO.File.WriteAllTextAsync(configFilePath, json);
        }
    }
}