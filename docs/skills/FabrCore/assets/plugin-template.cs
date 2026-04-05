using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

/// <summary>
/// {{PLUGIN_DESCRIPTION}}
/// </summary>
[PluginAlias("{{PLUGIN_ALIAS}}")]
[Description("{{PLUGIN_DESCRIPTION}}")]
[FabrCoreCapabilities("{{PLUGIN_CAPABILITIES}}")]
[FabrCoreNote("{{PLUGIN_NOTE}}")]
public class {{PLUGIN_NAME}} : IFabrCorePlugin
{
    private IFabrCoreAgentHost _agentHost = default!;
    private ILogger<{{PLUGIN_NAME}}> _logger = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<{{PLUGIN_NAME}}>>();

        // Read plugin-specific settings from config.Args using "{{PLUGIN_ALIAS}}:Key" convention
        // var apiKey = config.GetPluginSetting("{{PLUGIN_ALIAS}}", "ApiKey");

        return Task.CompletedTask;
    }

    [Description("{{TOOL_DESCRIPTION}}")]
    public async Task<string> {{TOOL_NAME}}(
        [Description("{{PARAM_DESCRIPTION}}")] string input)
    {
        _logger.LogInformation("{{TOOL_NAME}} called with: {Input}", input);

        // Set status message shown to client during long operations (instead of "Thinking..")
        _agentHost.SetStatusMessage("Processing..");
        try
        {
            // Tool implementation
            return "result";
        }
        finally
        {
            _agentHost.SetStatusMessage(null); // revert to default
        }
    }
}

// Register in AgentConfiguration:
// {
//   "Plugins": ["{{PLUGIN_ALIAS}}"],
//   "Args": {
//     "{{PLUGIN_ALIAS}}:ApiKey": "your-key"
//   }
// }
