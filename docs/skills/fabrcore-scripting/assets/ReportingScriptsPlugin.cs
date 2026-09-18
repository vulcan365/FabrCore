using FabrCore.Scripting;
using FabrCore.Sdk;

[PluginAlias("reporting-scripts")]
public sealed class ReportingScriptsPlugin : CSharpScriptingPluginBase
{
    protected override void Configure(ScriptingEnvironmentBuilder environment) => environment
        .AddPackage("Newtonsoft.Json", "13.0.3")
        .AddImports("Newtonsoft.Json.Linq")
        .WithTimeout(TimeSpan.FromSeconds(30))
        .WithMemoryLimit(512L * 1024 * 1024)
        .WithOutputLimit(32_768)
        .WithArtifactLimit(1_048_576)
        .WithInstructions("Use JObject.Parse(InputJson) to read authorized query results. " +
            "For orders, use data[\"orders\"]!.Values<decimal>().Sum(). " +
            "Return a small JSON-serializable summary. Check that inputs are complete.");
}
