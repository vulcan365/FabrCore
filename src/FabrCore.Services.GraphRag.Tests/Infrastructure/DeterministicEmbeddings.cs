using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.Tests.Infrastructure;

internal sealed class DeterministicEmbeddings : IEmbeddings
{
    public Task<Embedding<float>> GetEmbeddings(string text) =>
        Task.FromResult(new Embedding<float>(CreateVector(text)));

    public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts) =>
        Task.FromResult<IReadOnlyList<Embedding<float>>>(texts.Select(t => new Embedding<float>(CreateVector(t))).ToArray());

    private static float[] CreateVector(string text)
    {
        var vector = new float[TestEnvironment.EmbeddingDimensions];
        AddKeyword(vector, text, "apollo", 0);
        AddKeyword(vector, text, "orion", 1);
        AddKeyword(vector, text, "zephyr", 2);
        AddKeyword(vector, text, "database", 3);
        AddKeyword(vector, text, "security", 4);

        if (!vector.Take(5).Any(v => v > 0))
            vector[5] = 1;

        var magnitude = Math.Sqrt(vector.Sum(v => v * v));
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / magnitude);
        return vector;
    }

    private static void AddKeyword(float[] vector, string text, string keyword, int dimension)
    {
        if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            vector[dimension] = 1;
    }
}
