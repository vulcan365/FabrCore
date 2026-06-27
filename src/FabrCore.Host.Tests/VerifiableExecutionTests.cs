using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class VerifiableExecutionTests
{
    [TestMethod]
    public async Task SignedRecordsVerifySuccessfully()
    {
        using var signer = new LocalCertificateVerifiableExecutionSigner();
        var recorder = CreateRecorder(signer);

        await recorder.RecordAsync(new VerifiableExecutionRecord
        {
            TraceId = "trace-1",
            AgentHandle = "user:agent",
            Kind = ExecutionRecordKind.MessageInbound,
            Subject = "inbound",
            Metadata = new Dictionary<string, string?> { ["message.id"] = "m1" }
        });

        await recorder.RecordAsync(new VerifiableExecutionRecord
        {
            TraceId = "trace-1",
            AgentHandle = "user:agent",
            Kind = ExecutionRecordKind.MessageOutbound,
            Subject = "outbound",
            Metadata = new Dictionary<string, string?> { ["message.id"] = "m2" }
        });

        var result = await recorder.VerifyAsync("trace-1");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(ExecutionTrustLevel.SignedCustomIdentity, result.TrustLevel);
        Assert.AreEqual(2, result.VerifiedRecordCount);
        Assert.AreEqual(2, result.VerifiedSignatureCount);
    }

    [TestMethod]
    public async Task ModifiedRecordFailsVerification()
    {
        using var signer = new LocalCertificateVerifiableExecutionSigner();
        var store = new InMemoryVerifiableExecutionStore();
        var verifier = new VerifiableExecutionVerifier();
        var recorder = CreateRecorder(signer, store, verifier);

        await recorder.RecordAsync(new VerifiableExecutionRecord
        {
            TraceId = "trace-2",
            AgentHandle = "user:agent",
            Kind = ExecutionRecordKind.EventDelivered,
            Subject = "event",
            Metadata = new Dictionary<string, string?> { ["event.id"] = "e1" }
        });

        var bundle = await store.GetBundleAsync("trace-2");
        bundle.Records[0].Metadata["event.id"] = "tampered";

        var result = await verifier.VerifyAsync(bundle);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(ExecutionTrustLevel.Tampered, result.TrustLevel);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("digest mismatch", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task UnsignedRecordsRemainObservableButUntrusted()
    {
        var recorder = CreateRecorder(new NullVerifiableExecutionSigner());

        await recorder.RecordAsync(new VerifiableExecutionRecord
        {
            TraceId = "trace-3",
            AgentHandle = "user:agent",
            Kind = ExecutionRecordKind.ExternalDbEffect,
            Subject = "db:update",
            Metadata = new Dictionary<string, string?> { ["db.table"] = "Orders" }
        });

        var result = await recorder.VerifyAsync("trace-3");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(ExecutionTrustLevel.Unsigned, result.TrustLevel);
        Assert.AreEqual(1, result.VerifiedRecordCount);
        Assert.AreEqual(0, result.VerifiedSignatureCount);
    }

    private static VerifiableExecutionRecorder CreateRecorder(
        IVerifiableExecutionSigner signer,
        IVerifiableExecutionStore? store = null,
        IVerifiableExecutionVerifier? verifier = null)
    {
        return new VerifiableExecutionRecorder(
            store ?? new InMemoryVerifiableExecutionStore(),
            signer,
            verifier ?? new VerifiableExecutionVerifier(),
            Options.Create(new VerifiableExecutionOptions { Enabled = true }),
            NullLogger<VerifiableExecutionRecorder>.Instance);
    }
}
