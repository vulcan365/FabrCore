using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FabrCore.Sdk
{
    public interface IEmbeddings
    {
        Task<Embedding<float>> GetEmbeddings(string text);
    }

    public class Embeddings : IEmbeddings
    {
        private IEmbeddingGenerator<string, Embedding<float>>? embeddingClient;

        private readonly IFabrCoreChatClientService fabrcoreChatClientService;
        private readonly IServiceProvider serviceProvider;

        public Embeddings(IFabrCoreChatClientService fabrcoreChatClientService, IServiceProvider serviceProvider)
        {
            this.fabrcoreChatClientService = fabrcoreChatClientService;
            this.serviceProvider = serviceProvider;
        }

        public async Task<Embedding<float>> GetEmbeddings(string text)
        {
            if (embeddingClient == null)
            {
                embeddingClient = await fabrcoreChatClientService.GetEmbeddingsClient("OpenAIEmbeddings");
            }

            var val = await embeddingClient.GenerateAsync(text);


            return val;
        }
    }
}
