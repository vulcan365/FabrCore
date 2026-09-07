using System.Collections.Concurrent;

namespace FabrCore.Services.GraphRag.EvalConsole;

// Async-flow ownership keeps overlapping documents and their child requests isolated.
internal sealed class DocumentMeasurements : IDisposable
{
    private static readonly AsyncLocal<DocumentMeasurements?> Slot = new();
    private readonly DocumentMeasurements? previous = Slot.Value;
    public static DocumentMeasurements? Current => Slot.Value;
    public ConcurrentQueue<ChatCallSample> ChatCalls { get; } = new();
    public ConcurrentQueue<EmbeddingCallSample> EmbeddingCalls { get; } = new();
    public DocumentMeasurements() => Slot.Value = this;
    public void Dispose() => Slot.Value = previous;
}
