namespace Fabr.Sdk.Memory;

/// <summary>
/// Configuration options for the Fabr memory system.
/// </summary>
public sealed class MemoryOptions
{
    public string ConnectionString { get; set; } = "";
    public string EmbeddingConfigName { get; set; } = "OpenAIEmbeddings";
    public int Dimensions { get; set; } = 1536;
    public int ChunkSize { get; set; } = 500;
    public int ChunkOverlap { get; set; } = 64;
    public float VectorWeight { get; set; } = 0.7f;
    public float KeywordWeight { get; set; } = 0.3f;
    public int RrfK { get; set; } = 60;
}
