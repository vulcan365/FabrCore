using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.EvalConsole;

internal static class SelectionScaleExperiment
{
    internal static async Task<int> RunAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.local.json", true).AddEnvironmentVariables().Build();
        var modelFile = values.GetValueOrDefault("models", config["Eval:ModelConfigurationPath"] ?? "fabrcore.json");
        var models = JsonSerializer.Deserialize<FabrCoreConfiguration>(await File.ReadAllTextAsync(modelFile, ct), json)!;
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        var resolver = new LocalModelResolver(models);
        var clients = new MeasuredClients(new FabrCoreChatClientService(config, logs, resolver));
        var model = values.GetValueOrDefault("model", "default");
        var iterations = int.Parse(values.GetValueOrDefault("iterations", "3"));
        var minimal = bool.Parse(values.GetValueOrDefault("minimal-selection", "false"));
        var verify = bool.Parse(values.GetValueOrDefault("verify-selection", "false"));
        if (iterations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(iterations));
        var directory = Path.GetFullPath(Path.Combine(values.GetValueOrDefault("output", "artifacts/memory-selection-scale"),
            $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        var headers = Enumerable.Range(0, 200).Select(i => new MemoryHeader
        {
            MemoryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"selection-scale-v1:{i}"))[..16]),
            Title = $"Unrelated appliance {i} inspection",
            Type = MemoryType.Fact,
            Description = $"Appliance unit U-{i:D4} was inspected at depot D-{i:D3}. Its assigned inspector is operator P-{i:D4}.",
            UpdatedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc)
        }).ToList();
        headers[0].Title = "Atlas current production region";
        headers[0].Description = "As of September 7, Atlas production runs in westus3. The eastus deployment was retired.";
        headers[1].Title = "Atlas production region on September 1";
        headers[1].Description = "On September 1, Atlas production ran in eastus. This is a historical snapshot.";
        headers[1].IsPointInTime = true;
        headers[1].UpdatedAt = headers[1].UpdatedAt.AddDays(-6);
        headers[2].Title = "Atlas deployment procedure";
        headers[2].Type = MemoryType.Procedural;
        headers[2].Description = "To deploy Atlas, validate the migration, take a backup, deploy, then run the health probe.";
        var questions = new[]
        {
            new Question("current", "What is Atlas's current production region?", [headers[0].MemoryId]),
            new Question("historical", "Where did Atlas production run on September 1?", [headers[1].MemoryId]),
            new Question("procedure", "What steps must I follow to deploy Atlas?", [headers[2].MemoryId]),
            new Question("multi-part", "What is Atlas's current production region, and what steps must I follow to deploy Atlas?", [headers[0].MemoryId, headers[2].MemoryId]),
            new Question("comparison", "Compare Atlas's production region on September 1 with its current production region.", [headers[0].MemoryId, headers[1].MemoryId]),
            new Question("unknown", "What is Atlas owner's favorite ice cream flavor?", [])
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new { headers, questions }, json), ct);
        var report = new ScaleReport
        {
            PreferMinimalSelection = minimal,
            Model = $"{model}:{(await resolver.GetModelConfigurationAsync(model)).Model}",
            MemoryBinaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(IAgentMemoryService).Assembly.Location, ct)))
        };
        var reportPath = Path.Combine(directory, "report.json");
        try
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var random = new Random(90210 + iteration);
                var ordered = headers.OrderBy(_ => random.Next()).ToList();
                var variants = new List<(bool Compact, bool Verify)> { (false, false), (true, false) };
                if (verify) variants.Add((true, true));
                // Rotate execution order without changing the candidate order within a pass.
                foreach (var variant in variants.Skip(iteration % variants.Count).Concat(variants.Take(iteration % variants.Count)))
                {
                    var compact = variant.Compact;
                    var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<ILoggerFactory>(logs)
                        .AddSingleton<IFabrCoreChatClientService>(clients);
                    // Only the header selector is invoked. No hosted initializer, SQL read/write or embeddings.
                    collection.AddAgentMemoryServices("MemoryEvalDb", options => {
                        options.Models.RelevanceModelName = model;
                        options.Retrieval.UseCompactSelectionIds = compact;
                        options.Retrieval.PreferMinimalSelection = minimal;
                        options.Retrieval.VerifyMultiMemorySelection = variant.Verify;
                    });
                    await using var services = collection.BuildServiceProvider();
                    var retriever = services.GetRequiredService<IMemoryRetriever>();
                    foreach (var question in questions)
                    {
                        var selected = await retriever.SelectRelevantMemoriesAsync(question.Query, ordered, 5, ct: ct);
                        var calls = new List<Call>();
                        while (clients.Calls.TryDequeue(out var call)) calls.Add(call);
                        var passed = selected.ToHashSet().SetEquals(question.Expected) && calls.Count >= 1
                            && calls.Count <= (variant.Verify ? 2 : 1) && calls.All(call => call.Error is null);
                        report.Cases.Add(new(iteration + 1, compact, variant.Verify, question.Id, passed, selected, calls, ordered.Select(h => h.MemoryId).ToArray()));
                        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json), ct);
                        Console.WriteLine($"{iteration + 1}/{(variant.Verify ? "verified" : compact ? "compact" : "control")}/{question.Id}: {(passed ? "PASS" : "FAIL")}");
                    }
                }
            }
            report.Status = "complete";
        }
        catch (Exception ex) { report.Status = "failed"; report.Error = ex.GetType().Name; throw; }
        finally { await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json), CancellationToken.None); }
        Console.WriteLine($"Scale experiment: {reportPath}");
        return report.Cases.All(c => c.Passed) ? 0 : 2;
    }

    private sealed record Question(string Id, string Query, Guid[] Expected);
    private sealed record Case(int Iteration, bool Compact, bool Verified, string Question, bool Passed, IReadOnlyList<Guid> Selected, List<Call> Calls, Guid[] HeaderOrder);
    private sealed class ScaleReport
    {
        public string Version { get; } = "selection-scale-v2";
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
        public string Model { get; set; } = "";
        public string MemoryBinaryHash { get; set; } = "";
        public bool PreferMinimalSelection { get; set; }
        public List<Case> Cases { get; } = [];
    }
}
