using Azure.AI.OpenAI;
using FabrCore.Core;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace FabrCore.Sdk.Tests.Infrastructure;

/// <summary>
/// A real OpenAI or Azure OpenAI chat client over a scripted HTTP transport, so tests see provider
/// failures exactly as the SDK surfaces them rather than as hand-built exceptions.
/// </summary>
internal static class ScriptedProvider
{
    public sealed record Reply(int Status, string Body, string ContentType = "application/json");

#pragma warning disable OPENAI001
    public static IChatClient Client(string provider, string? chatApi, params Reply[] replies)
    {
        var transport = new HttpClientPipelineTransport(new HttpClient(new Handler(replies)));
        OpenAIClient providerClient = provider switch
        {
            "OpenAI" => new OpenAIClient(
                new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://openai.test/v1"),
                    Transport = transport,
                    RetryPolicy = new ClientRetryPolicy(0)
                }),
            "Azure" => new AzureOpenAIClient(
                new Uri("https://azure.test"),
                new ApiKeyCredential("test-key"),
                new AzureOpenAIClientOptions { Transport = transport, RetryPolicy = new ClientRetryPolicy(0) }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

        return FabrCoreChatClientService.CreateChatClient(providerClient, new ModelConfiguration
        {
            Name = "default",
            Provider = provider,
            Uri = "https://openai.test/v1",
            Model = "gpt-test",
            ApiKeyAlias = "test-key",
            ChatApi = chatApi
        });
    }
#pragma warning restore OPENAI001

    /// <summary>The exception the SDK throws for one failed call with the given status and body.</summary>
    public static async Task<Exception> FailureAsync(
        int status,
        string body,
        string provider = "OpenAI",
        string? chatApi = null,
        bool streaming = false,
        string contentType = "application/json")
    {
        var client = Client(provider, chatApi, new Reply(status, body, contentType));
        try
        {
            if (streaming)
            {
                await foreach (var _ in client.GetStreamingResponseAsync("hello")) { }
            }
            else
            {
                await client.GetResponseAsync("hello");
            }
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"The scripted provider reply ({status}) did not fail the call.");
    }

    private sealed class Handler(Reply[] replies) : HttpMessageHandler
    {
        private readonly Queue<Reply> queue = new(replies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reply = queue.Dequeue();
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)reply.Status)
            {
                Content = new StringContent(reply.Body, Encoding.UTF8, reply.ContentType)
            });
        }
    }
}
