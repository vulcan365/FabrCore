using Azure.AI.OpenAI;
using FabrCore.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ModelDefaultsChatClientTests
{
    [TestMethod]
    public async Task DirectCall_AppliesDefaultsWithoutAddingToolsOrMutatingCallerOptions()
    {
        var inner = new RecordingChatClient();
        var client = ModelDefaultsChatClient.Apply(inner, CreateConfiguration());
        var callerReasoning = new ReasoningOptions { Output = ReasoningOutput.Summary };
        var callerOptions = new ChatOptions { Reasoning = callerReasoning };

        await client.GetResponseAsync("classify this", callerOptions);

        Assert.AreEqual(1000, inner.LastOptions?.MaxOutputTokens);
        Assert.AreEqual(ReasoningEffort.None, inner.LastOptions?.Reasoning?.Effort);
        Assert.AreEqual(ReasoningOutput.Summary, inner.LastOptions?.Reasoning?.Output);
        Assert.IsNull(inner.LastOptions?.Tools);
        Assert.IsNull(callerOptions.MaxOutputTokens);
        Assert.IsNull(callerReasoning.Effort);
        Assert.AreNotSame(callerOptions, inner.LastOptions);
        Assert.AreNotSame(callerReasoning, inner.LastOptions?.Reasoning);
    }

    [TestMethod]
    public async Task DirectCall_ExplicitPerCallOptionsOverrideDefaults()
    {
        var inner = new RecordingChatClient();
        var client = ModelDefaultsChatClient.Apply(inner, CreateConfiguration());
        var options = new ChatOptions
        {
            MaxOutputTokens = 250,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High }
        };

        await client.GetResponseAsync("classify this", options);

        Assert.AreEqual(250, inner.LastOptions?.MaxOutputTokens);
        Assert.AreEqual(ReasoningEffort.High, inner.LastOptions?.Reasoning?.Effort);
        Assert.AreNotSame(options.Reasoning, inner.LastOptions?.Reasoning);
    }

    [TestMethod]
    [DataRow("xhigh")]
    [DataRow("ExtraHigh")]
    public async Task ExtraHighAliases_AreAccepted(string value)
    {
        var inner = new RecordingChatClient();
        var client = ModelDefaultsChatClient.Apply(
            inner,
            CreateConfiguration(reasoningEffort: value));

        await client.GetResponseAsync("classify this");

        Assert.AreEqual(ReasoningEffort.ExtraHigh, inner.LastOptions?.Reasoning?.Effort);
    }

    [TestMethod]
    public async Task StreamingCall_AppliesDefaults()
    {
        var inner = new RecordingChatClient();
        var client = ModelDefaultsChatClient.Apply(inner, CreateConfiguration());

        await foreach (var _ in client.GetStreamingResponseAsync("classify this"))
        {
        }

        Assert.AreEqual(1000, inner.LastOptions?.MaxOutputTokens);
        Assert.AreEqual(ReasoningEffort.None, inner.LastOptions?.Reasoning?.Effort);
    }

    [TestMethod]
    public void NoDefaults_ReturnsOriginalClient()
    {
        var inner = new RecordingChatClient();
        var client = ModelDefaultsChatClient.Apply(inner, CreateConfiguration(
            maxOutputTokens: null,
            reasoningEffort: null));

        Assert.AreSame(inner, client);
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("minimal")]
    public void InvalidReasoningEffort_FailsClearly(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModelDefaultsChatClient.Apply(
                new RecordingChatClient(),
                CreateConfiguration(reasoningEffort: value)));

        StringAssert.Contains(exception.Message, "graphrag");
        StringAssert.Contains(exception.Message, value);
        StringAssert.Contains(exception.Message, "Supported values");
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Azure")]
    public async Task ProviderRequest_ContainsConfiguredDefaultsAndNoTools(string provider)
    {
        var handler = new RecordingProviderHandler();
        var providerClient = CreateProviderClient(provider, handler);
        var client = ModelDefaultsChatClient.Apply(providerClient, CreateConfiguration());

        await client.GetResponseAsync("classify this");

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.AreEqual(1000, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.AreEqual("none", root.GetProperty("reasoning_effort").GetString());
        Assert.IsFalse(root.TryGetProperty("tools", out _));
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Azure")]
    public async Task ProviderRequest_PerCallOptionsOverrideConfiguredDefaults(string provider)
    {
        var handler = new RecordingProviderHandler();
        var providerClient = CreateProviderClient(provider, handler);
        var client = ModelDefaultsChatClient.Apply(providerClient, CreateConfiguration());

        await client.GetResponseAsync("classify this", new ChatOptions
        {
            MaxOutputTokens = 250,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High }
        });

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.AreEqual(250, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.AreEqual("high", root.GetProperty("reasoning_effort").GetString());
    }

    [TestMethod]
    public async Task SdkModelConfigResponse_DeserializesReasoningEffort()
    {
        var handler = new ModelConfigHandler();
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCoreHostUrl"] = "https://fabrcore.test"
            })
            .Build();
        var service = new FabrCoreChatClientService(
            configuration,
            NullLoggerFactory.Instance,
            httpClient);

        var result = await service.GetModelConfigurationAsync("graphrag");

        Assert.AreEqual("none", result.ReasoningEffort);
        Assert.AreEqual(1000, result.MaxOutputTokens);
        Assert.AreEqual(
            "https://fabrcore.test/fabrcoreapi/ModelConfig/model/graphrag",
            handler.RequestUri?.ToString());
    }

    [TestMethod]
    public async Task GetChatClient_InvalidReasoningEffortFailsBeforeApiKeyFetch()
    {
        var handler = new ModelConfigHandler("invalid");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCoreHostUrl"] = "https://fabrcore.test"
            })
            .Build();
        var service = new FabrCoreChatClientService(
            configuration,
            NullLoggerFactory.Instance,
            new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetChatClient("graphrag"));

        StringAssert.Contains(exception.Message, "unsupported ReasoningEffort");
        Assert.AreEqual(1, handler.RequestCount);
    }

    private static ModelConfiguration CreateConfiguration(
        int? maxOutputTokens = 1000,
        string? reasoningEffort = "none")
        => new()
        {
            Name = "graphrag",
            Provider = "OpenAI",
            Uri = "https://openai.test/v1",
            Model = "gpt-test",
            ApiKeyAlias = "test-key",
            MaxOutputTokens = maxOutputTokens,
            ReasoningEffort = reasoningEffort
        };

#pragma warning disable OPENAI001
    private static IChatClient CreateProviderClient(string provider, RecordingProviderHandler handler)
    {
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));

        return provider switch
        {
            "OpenAI" => new OpenAIClient(
                    new ApiKeyCredential("test-key"),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri("https://openai.test/v1"),
                        Transport = transport
                    })
                .GetChatClient("gpt-test")
                .AsIChatClient(),
            "Azure" => new AzureOpenAIClient(
                    new Uri("https://azure.test"),
                    new ApiKeyCredential("test-key"),
                    new AzureOpenAIClientOptions { Transport = transport })
                .GetChatClient("gpt-test")
                .AsIChatClient(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }
#pragma warning restore OPENAI001

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingProviderHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "chatcmpl-test",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "gpt-test",
                      "choices": [
                        {
                          "index": 0,
                          "message": { "role": "assistant", "content": "ok" },
                          "finish_reason": "stop",
                          "logprobs": null
                        }
                      ],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class ModelConfigHandler(string reasoningEffort = "none") : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "name": "graphrag",
                      "provider": "Azure",
                      "uri": "https://azure.test",
                      "model": "gpt-test",
                      "apiKeyAlias": "test-key",
                      "timeoutSeconds": 60,
                      "maxOutputTokens": 1000,
                      "reasoningEffort": "{{reasoningEffort}}"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
