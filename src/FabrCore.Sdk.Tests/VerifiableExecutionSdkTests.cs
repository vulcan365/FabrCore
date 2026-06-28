using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk.VerifiableExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class VerifiableExecutionSdkTests
{
    [TestMethod]
    public async Task RecordDbEffectAsyncRecordsSuccessMetadata()
    {
        var context = new RecordingVerifiableExecutionContext();

        var result = await context.RecordDbEffectAsync(
            operation: "UpdateOrderStatus",
            target: "Orders",
            subject: "order-1",
            effect: () => Task.FromResult("updated"),
            metadata: new Dictionary<string, string?> { ["status_hash"] = VerifiableExecutionHash.HashText("paid") });

        Assert.AreEqual("updated", result.Value);
        Assert.IsTrue(result.EvidenceRecorded);
        Assert.AreEqual(ExecutionRecordKind.ExternalDbEffect, context.ExternalEffects.Single().Kind);
        Assert.AreEqual("order-1", context.ExternalEffects.Single().Subject);
        Assert.AreEqual("UpdateOrderStatus", context.ExternalEffects.Single().Metadata["operation"]);
        Assert.AreEqual("Orders", context.ExternalEffects.Single().Metadata["target"]);
        Assert.AreEqual("success", context.ExternalEffects.Single().Metadata["status"]);
        Assert.IsTrue(context.ExternalEffects.Single().Metadata.ContainsKey("result_hash"));
    }

    [TestMethod]
    public async Task RecordLibraryCallAsyncRecordsFailureAndRethrowsOriginalException()
    {
        var context = new RecordingVerifiableExecutionContext();
        var expected = new InvalidOperationException("library failed");

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            context.RecordLibraryCallAsync<string>(
                operation: "CalculateRiskScore",
                componentName: "RiskScoringEngine",
                method: "ScoreAsync",
                call: () => throw expected));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(ExecutionRecordKind.ExternalLibraryCall, context.ExternalEffects.Single().Kind);
        Assert.AreEqual("failure", context.ExternalEffects.Single().Metadata["status"]);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, context.ExternalEffects.Single().Metadata["error_type"]);
    }

    [TestMethod]
    public async Task EvidenceRecorderFailureDoesNotFailBusinessOperation()
    {
        var context = new RecordingVerifiableExecutionContext { ThrowOnRecord = true };

        var result = await context.RecordStorageEffectAsync(
            operation: "WriteBlob",
            target: "container",
            subject: "path/to/blob",
            effect: () => Task.FromResult(42));

        Assert.AreEqual(42, result.Value);
        Assert.IsFalse(result.EvidenceRecorded);
    }

    [TestMethod]
    public async Task RecordHttpCallAsyncSanitizesUrlAndRecordsStatusCode()
    {
        var context = new RecordingVerifiableExecutionContext();
        var url = new Uri("https://api.example.com/invoices/123?token=secret#frag");

        var result = await context.RecordHttpCallAsync(
            operation: "FetchInvoice",
            method: "GET",
            url,
            call: () => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)));

        Assert.IsTrue(result.Value?.IsSuccessStatusCode);
        var effect = context.ExternalEffects.Single();
        Assert.AreEqual(ExecutionRecordKind.ExternalHttpCall, effect.Kind);
        Assert.AreEqual("https://api.example.com/invoices/123", effect.Metadata["target"]);
        Assert.AreEqual("GET", effect.Metadata["method"]);
        Assert.AreEqual("202", effect.Metadata["status_code"]);
        Assert.AreEqual(bool.TrueString, effect.Metadata["http_success"]);
        Assert.IsFalse(effect.Metadata.Values.Any(v => v?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true));
    }

    [TestMethod]
    public async Task PluginToolInvocationRecordsPluginCall()
    {
        var context = new RecordingVerifiableExecutionContext();
        var registry = new FabrCoreToolRegistry(NullLogger<FabrCoreToolRegistry>.Instance);
        var services = CreateServices(context);

        var tools = await registry.ResolveToolsAsync(
            services,
            pluginAliases: ["verifiable-test-plugin"],
            toolAliases: null,
            new AgentConfiguration());

        var function = Assert.IsInstanceOfType<AIFunction>(tools.Single());
        var result = await function.InvokeAsync(new AIFunctionArguments { ["input"] = "abc" });

        Assert.AreEqual("plugin:abc", result?.ToString());
        Assert.AreEqual(ExecutionRecordKind.PluginCall, context.ExternalEffects.Single().Kind);
        Assert.AreEqual("verifiable-test-plugin", context.ExternalEffects.Single().Metadata["component_name"]);
        Assert.AreEqual("Echo", context.ExternalEffects.Single().Metadata["method"]);
        Assert.AreEqual("success", context.ExternalEffects.Single().Metadata["status"]);
        Assert.IsTrue(context.ExternalEffects.Single().Metadata.ContainsKey("argument_hash"));
        Assert.IsTrue(context.ExternalEffects.Single().Metadata.ContainsKey("result_hash"));
    }

    [TestMethod]
    public async Task StaticToolInvocationRecordsToolCall()
    {
        var context = new RecordingVerifiableExecutionContext();
        var registry = new FabrCoreToolRegistry(NullLogger<FabrCoreToolRegistry>.Instance);
        var services = CreateServices(context);

        var tools = await registry.ResolveToolsAsync(
            services,
            pluginAliases: null,
            toolAliases: ["verifiable-test-tool"],
            new AgentConfiguration());

        var function = Assert.IsInstanceOfType<AIFunction>(tools.Single());
        var result = await function.InvokeAsync(new AIFunctionArguments { ["input"] = "xyz" });

        Assert.AreEqual("tool:xyz", result?.ToString());
        Assert.AreEqual(ExecutionRecordKind.ToolCall, context.ExternalEffects.Single().Kind);
        Assert.AreEqual("verifiable-test-tool", context.ExternalEffects.Single().Metadata["component_name"]);
        Assert.AreEqual("ToolEcho", context.ExternalEffects.Single().Metadata["method"]);
    }

    private static IServiceProvider CreateServices(IVerifiableExecutionContext context)
        => new ServiceCollection()
            .AddSingleton(context)
            .BuildServiceProvider();

    [PluginAlias("verifiable-test-plugin")]
    public sealed class VerifiableTestPlugin : IFabrCorePlugin
    {
        public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
            => Task.CompletedTask;

        [System.ComponentModel.Description("Echoes input for verifiable execution tests.")]
        public string Echo([System.ComponentModel.Description("Input text")] string input)
            => $"plugin:{input}";
    }

    public static class VerifiableTestTools
    {
        [ToolAlias("verifiable-test-tool")]
        [System.ComponentModel.Description("Echoes input for verifiable execution tests.")]
        public static string ToolEcho([System.ComponentModel.Description("Input text")] string input)
            => $"tool:{input}";
    }

    private sealed class RecordingVerifiableExecutionContext : IVerifiableExecutionContext
    {
        public bool ThrowOnRecord { get; set; }
        public List<(ExecutionRecordKind Kind, string Subject, IReadOnlyDictionary<string, string?> Metadata)> ExternalEffects { get; } = new();

        public Task<VerifiableExecutionEnvelope?> RecordAsync(
            VerifiableExecutionRecord record,
            CancellationToken cancellationToken = default)
            => Task.FromResult<VerifiableExecutionEnvelope?>(new VerifiableExecutionEnvelope { RecordId = record.Id });

        public Task<VerifiableExecutionEnvelope?> RecordExternalEffectAsync(
            ExecutionRecordKind kind,
            string subject,
            IReadOnlyDictionary<string, string?> metadata,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRecord)
                throw new InvalidOperationException("record failed");

            ExternalEffects.Add((kind, subject, new Dictionary<string, string?>(metadata, StringComparer.Ordinal)));
            return Task.FromResult<VerifiableExecutionEnvelope?>(new VerifiableExecutionEnvelope
            {
                RecordId = Guid.NewGuid().ToString(),
                SignerIdentityKind = VerifiableExecutionSignerIdentityKind.LocalCertificate
            });
        }

        public Task<VerifiableExecutionVerificationResult> VerifyAsync(
            string traceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new VerifiableExecutionVerificationResult { TraceId = traceId, IsValid = true });
    }

}
