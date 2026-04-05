using System.ComponentModel;
using FabrCore.Sdk;

/// <summary>
/// {{TOOL_CLASS_DESCRIPTION}}
/// </summary>
public static class {{TOOL_CLASS_NAME}}
{
    [ToolAlias("{{TOOL_ALIAS}}")]
    [Description("{{TOOL_DESCRIPTION}}")]
    [FabrCoreCapabilities("{{TOOL_CAPABILITIES}}")]
    [FabrCoreNote("{{TOOL_NOTE}}")]
    public static string {{TOOL_METHOD_NAME}}(
        [Description("{{PARAM_DESCRIPTION}}")] string input)
    {
        // Tool implementation — must be static
        return "result";
    }
}

// Register in AgentConfiguration:
// {
//   "Tools": ["{{TOOL_ALIAS}}"]
// }
