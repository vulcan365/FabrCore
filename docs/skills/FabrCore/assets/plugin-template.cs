using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk;
using FabrCore.Sdk.VerifiableExecution;
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
    private IVerifiableExecutionContext? _verifiableExecution;
    private ILogger<{{PLUGIN_NAME}}> _logger = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        _verifiableExecution = serviceProvider.GetService<IVerifiableExecutionContext>();
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
            var result = await _verifiableExecution.RecordLibraryCallAsync(
                operation: "{{TOOL_NAME}}",
                componentName: "{{PLUGIN_NAME}}",
                method: "{{TOOL_NAME}}",
                call: () => Task.FromResult("result"));

            return result.Value ?? "result";
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
