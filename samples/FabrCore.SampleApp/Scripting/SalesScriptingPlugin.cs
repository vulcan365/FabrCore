using System.ComponentModel;
using System.Text.Json;
using FabrCore.Scripting;
using FabrCore.Sdk;

namespace FabrCore.SampleApp.Scripting;

[PluginAlias(Alias)]
[Description("C# calculations over a complete, fictional sales dataset using Newtonsoft.Json.")]
public sealed class SalesScriptingPlugin : CSharpScriptingPluginBase
{
    public const string Alias = "sample-sales-scripts";

    protected override void Configure(ScriptingEnvironmentBuilder environment) => environment
        .AddPackage("Newtonsoft.Json", "13.0.3")
        .AddImports("Newtonsoft.Json.Linq")
        .WithTimeout(TimeSpan.FromSeconds(30))
        .WithMemoryLimit(512L * 1024 * 1024)
        .WithOutputLimit(32_768)
        .WithArtifactLimit(1_048_576)
        .WithInstructions("""
            Call GetDemoSales to obtain the complete fictional USD sales dataset, then pass it
            as ExecuteCSharp input. Parse InputJson with JObject.Parse; rows are in ["orders"].
            Each row has id, category, quantity (integer), and unitPrice (decimal).
            Revenue is quantity * unitPrice; use decimal arithmetic, not double.
            Scripts start fresh each call. Return a small JSON-serializable summary.
            Only write requested reports under OutputDirectory. Returned artifacts are available
            to the calling application; this demo chat does not publish download links.
            Example calculation and report:
            """ + "\n" + SalesReportScript);

    [Description("Get all three fictional sales orders in USD. This is the complete dataset, not a page. Pass the returned object as C# script input.")]
    public JsonElement GetDemoSales() => JsonSerializer.SerializeToElement(new
    {
        currency = "USD",
        orders = new[]
        {
            new { id = "SALE-001", category = "Bikes", quantity = 1, unitPrice = 250m },
            new { id = "SALE-002", category = "Accessories", quantity = 2, unitPrice = 50m },
            new { id = "SALE-003", category = "Accessories", quantity = 2, unitPrice = 25m }
        }
    });

    // Used in the tool's guidance and exercised through the real tool registry by the sample test.
    public const string SalesReportScript = """
        var data = JObject.Parse(InputJson);
        var orders = (JArray)data["orders"]!;
        var total = orders.Sum(order => order["quantity"]!.Value<int>() * order["unitPrice"]!.Value<decimal>());
        var byCategory = orders.GroupBy(order => order["category"]!.Value<string>()!)
            .ToDictionary(group => group.Key,
                group => group.Sum(order => order["quantity"]!.Value<int>() * order["unitPrice"]!.Value<decimal>()));
        var report = new { Currency = data["currency"]!.Value<string>(), OrderCount = orders.Count, Total = total, ByCategory = byCategory };
        System.IO.File.WriteAllText(System.IO.Path.Combine(OutputDirectory, "sales-report.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
        Console.WriteLine("Calculated all " + orders.Count + " sample orders.");
        return new { Report = report, WorkerProcessId = System.Environment.ProcessId };
        """;
}
