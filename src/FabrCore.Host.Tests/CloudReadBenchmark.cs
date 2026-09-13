using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize, TestCategory("Evaluation")]
public sealed class CloudReadBenchmark
{
    [TestMethod]
    public async Task MeasureSingleSiloEchoWithRetainedMonitorReads()
    {
        var monitor = new InMemoryAgentMessageMonitor(NullLogger<InMemoryAgentMessageMonitor>.Instance, 10000);
        await using var app = DatabaseModeTests.BuildHost("Development", configureServices: services => services.AddSingleton<IAgentMessageMonitor>(monitor));
        await app.StartAsync();
        try
        {
            var agents = app.Services.GetRequiredService<IFabrCoreAgentService>();
            await agents.ConfigureAgentsAsync("benchmark", Enumerable.Range(0, 16).Select(i => new AgentConfiguration { Handle = "echo" + i, AgentType = "database-mode-echo" }).ToList());
            using var http = app.GetTestClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
            var client = new FabrCoreAdministrationClient(http);
            async Task<(double Rate, double P95, int Reads)> Measure(bool reads, int seconds)
            {
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                var samples = new ConcurrentBag<double>(); var queries = 0;
                var timer = Stopwatch.StartNew();
                var reader = reads ? Task.Run(async () =>
                {
                    try
                    {
                        while (!end.IsCancellationRequested)
                        {
                            await client.QueryMonitorAsync(new() { Principal = "benchmark", Limit = 100 }, end.Token);
                            Interlocked.Increment(ref queries);
                            await Task.Delay(1000, end.Token);
                        }
                    }
                    catch (OperationCanceledException) when (end.IsCancellationRequested) { }
                }) : Task.CompletedTask;
                await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
                {
                    while (!end.IsCancellationRequested)
                    {
                        var start = Stopwatch.GetTimestamp();
                        await agents.SendAndReceiveMessageAsync("benchmark", "echo" + i, new AgentMessage { Message = "benchmark" });
                        samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                }));
                await reader;
                var ordered = samples.Order().ToArray();
                return (ordered.Length / timer.Elapsed.TotalSeconds, ordered[(int)(ordered.Length * .95)], queries);
            }
            await Measure(false, 3);
            var before = await Measure(false, 6);
            var loaded = await Measure(true, 6);
            var after = await Measure(false, 6);
            var baselineRate = (before.Rate + after.Rate) / 2; var baselineP95 = (before.P95 + after.P95) / 2;
            var report = new { timestamp = DateTimeOffset.UtcNow,
                load = "One local silo; 16 echo agents; one sequential caller each; 10,000-record monitor; one authenticated 100-record HTTP query/second; no LLM. Baseline/read/baseline after warmup.",
                baselineMessagesPerSecond = baselineRate, withReadsMessagesPerSecond = loaded.Rate,
                baselineP95Milliseconds = baselineP95, withReadsP95Milliseconds = loaded.P95,
                throughputRegressionPercent = (baselineRate - loaded.Rate) / baselineRate * 100,
                p95RegressionPercent = (loaded.P95 - baselineP95) / baselineP95 * 100, queries = loaded.Reads,
                limitation = "Synthetic single-host measurement. It does not certify SQL, model workloads, or multi-silo production performance." };
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "fabrcore-cloud-benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await app.StopAsync(); }
    }
}
