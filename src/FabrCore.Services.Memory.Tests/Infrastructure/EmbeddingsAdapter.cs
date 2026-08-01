using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal sealed class EmbeddingsAdapter : IEmbeddings
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public EmbeddingsAdapter(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _generator = generator;
    }

    public async Task<Embedding<float>> GetEmbeddings(string text) =>
        await _generator.GenerateAsync(text);

    public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts) =>
        await _generator.GenerateAsync(texts);
}
