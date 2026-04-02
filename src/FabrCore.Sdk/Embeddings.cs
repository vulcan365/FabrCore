using Microsoft.Extensions.AI;

namespace FabrCore.Sdk
{
    public interface IEmbeddings
    {
        Task<Embedding<float>> GetEmbeddings(string text);
        Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts);
    }

    public class Embeddings : IEmbeddings
    {
        private IEmbeddingGenerator<string, Embedding<float>>? embeddingClient;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        private readonly IFabrCoreChatClientService fabrcoreChatClientService;

        public Embeddings(IFabrCoreChatClientService fabrcoreChatClientService)
        {
            this.fabrcoreChatClientService = fabrcoreChatClientService;
        }

        private async Task<IEmbeddingGenerator<string, Embedding<float>>> GetClientAsync()
        {
            if (embeddingClient != null)
                return embeddingClient;

            await _initLock.WaitAsync();
            try
            {
                embeddingClient ??= await fabrcoreChatClientService.GetEmbeddingsClient("embeddings");
                return embeddingClient;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<Embedding<float>> GetEmbeddings(string text)
        {
            var client = await GetClientAsync();
            return await client.GenerateAsync(text);
        }

        public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            var client = await GetClientAsync();
            var result = await client.GenerateAsync(texts);
            return result;
        }
    }
}
