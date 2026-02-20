using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fabr.Sdk
{
    public interface IEmbeddings
    {
        Task<Embedding<float>> GetEmbeddings(string text);
    }

    public class Embeddings : IEmbeddings
    {
        private IEmbeddingGenerator<string, Embedding<float>>? embeddingClient;

        private readonly IFabrChatClientService fabrChatClientService;
        private readonly IServiceProvider serviceProvider;

        public Embeddings(IFabrChatClientService fabrChatClientService, IServiceProvider serviceProvider)
        {
            this.fabrChatClientService = fabrChatClientService;
            this.serviceProvider = serviceProvider;
        }

        public async Task<Embedding<float>> GetEmbeddings(string text)
        {
            if (embeddingClient == null)
            {
                embeddingClient = await fabrChatClientService.GetEmbeddingsClient("OpenAIEmbeddings");
            }

            var val = await embeddingClient.GenerateAsync(text);


            return val;
        }
    }
}
