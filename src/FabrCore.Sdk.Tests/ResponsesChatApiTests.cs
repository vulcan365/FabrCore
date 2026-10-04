using Azure.AI.OpenAI;
using FabrCore.Core;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ResponsesChatApiTests
{
    private const string ToolCallResponse =
        """
        {
          "id": "resp_1", "object": "response", "created_at": 1, "status": "completed", "model": "gpt-test",
          "output": [
            { "type": "reasoning", "id": "rs_1", "summary": [], "encrypted_content": "encrypted-reasoning" },
            { "type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "ping", "arguments": "{}", "status": "completed" }
          ],
          "usage": { "input_tokens": 10, "output_tokens": 20, "total_tokens": 30,
                     "output_tokens_details": { "reasoning_tokens": 12 } }
        }
        """;

    private const string TextResponse =
        """
        {
          "id": "resp_2", "object": "response", "created_at": 2, "status": "completed", "model": "gpt-test",
          "output": [
            { "type": "message", "id": "msg_1", "role": "assistant", "status": "completed",
              "content": [ { "type": "output_text", "text": "pong", "annotations": [] } ] }
          ],
          "usage": { "input_tokens": 30, "output_tokens": 2, "total_tokens": 32 }
        }
        """;

    [TestMethod]
    [DataRow(null)]
    [DataRow("ChatCompletions")]
    public async Task DefaultChatApi_CallsChatCompletions(string? chatApi)
    {
        var handler = new RecordingProviderHandler(
            """
            { "id": "chatcmpl-test", "object": "chat.completion", "created": 1, "model": "gpt-test",
              "choices": [ { "index": 0, "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" } ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 } }
            """);
        var client = CreateClient("OpenAI", handler, CreateConfiguration(chatApi));

        await client.GetResponseAsync("hello");

        Assert.AreEqual("/v1/chat/completions", handler.Requests.Single().Path);
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Azure")]
    public async Task ResponsesChatApi_SendsStatelessRequestWithReasoningEffortAndTools(string provider)
    {
        var handler = new RecordingProviderHandler(TextResponse);
        var client = CreateClient(provider, handler, CreateConfiguration("Responses"));

        var response = await client.GetResponseAsync("Call the ping tool.", new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(Ping, "ping")]
        });

        var request = handler.Requests.Single();
        StringAssert.EndsWith(request.Path, "/responses");

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.AreEqual("gpt-test", root.GetProperty("model").GetString());
        Assert.AreEqual("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.AreEqual(1000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.IsFalse(root.GetProperty("store").GetBoolean(), "FabrCore owns the history; nothing is stored at the provider");
        CollectionAssert.Contains(
            root.GetProperty("include").EnumerateArray().Select(i => i.GetString()).ToList(),
            "reasoning.encrypted_content");
        Assert.IsFalse(root.TryGetProperty("previous_response_id", out _));

        var tool = root.GetProperty("tools").EnumerateArray().Single();
        Assert.AreEqual("function", tool.GetProperty("type").GetString());
        Assert.AreEqual("ping", tool.GetProperty("name").GetString());

        Assert.AreEqual("pong", response.Text);
        Assert.IsNull(response.ConversationId, "a provider conversation id would hand history to the provider");
    }

    [TestMethod]
    public async Task ResponsesChatApi_ToolLoopCarriesReasoningItemsBackToTheProvider()
    {
        var handler = new RecordingProviderHandler(ToolCallResponse, TextResponse);
        var client = CreateClient("OpenAI", handler, CreateConfiguration("Responses"))
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var response = await client.GetResponseAsync("Call the ping tool.", new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(Ping, "ping")]
        });

        Assert.AreEqual("pong", response.Text);
        Assert.HasCount(2, handler.Requests);

        using var document = JsonDocument.Parse(handler.Requests[1].Body);
        var root = document.RootElement;
        Assert.IsFalse(root.GetProperty("store").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("previous_response_id", out _));
        Assert.AreEqual("low", root.GetProperty("reasoning").GetProperty("effort").GetString());

        var input = root.GetProperty("input").EnumerateArray().ToList();
        var types = input.Select(i => i.TryGetProperty("type", out var type) ? type.GetString() : "message").ToList();
        CollectionAssert.AreEqual(
            new[] { "message", "reasoning", "function_call", "function_call_output" }, types);
        Assert.AreEqual("encrypted-reasoning", input[1].GetProperty("encrypted_content").GetString());
        Assert.AreEqual("call_1", input[2].GetProperty("call_id").GetString());
        Assert.AreEqual("call_1", input[3].GetProperty("call_id").GetString());
        StringAssert.Contains(input[3].GetProperty("output").GetString(), "pong");
    }

    [TestMethod]
    public void UnsupportedChatApi_FailsClearly()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModelDefaultsChatClient.ValidateConfiguration(CreateConfiguration("assistants")));

        StringAssert.Contains(exception.Message, "default");
        StringAssert.Contains(exception.Message, "assistants");
        StringAssert.Contains(exception.Message, "ChatCompletions, Responses");
    }

    private static string Ping() => "pong";

    private static ModelConfiguration CreateConfiguration(string? chatApi) => new()
    {
        Name = "default",
        Provider = "OpenAI",
        Uri = "https://openai.test/v1",
        Model = "gpt-test",
        ApiKeyAlias = "test-key",
        MaxOutputTokens = 1000,
        ReasoningEffort = "low",
        ChatApi = chatApi
    };

#pragma warning disable OPENAI001
    private static IChatClient CreateClient(
        string provider, RecordingProviderHandler handler, ModelConfiguration configuration)
    {
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        OpenAIClient providerClient = provider switch
        {
            "OpenAI" => new OpenAIClient(
                new ApiKeyCredential("test-key"),
                new OpenAIClientOptions { Endpoint = new Uri("https://openai.test/v1"), Transport = transport }),
            "Azure" => new AzureOpenAIClient(
                new Uri("https://azure.test"),
                new ApiKeyCredential("test-key"),
                new AzureOpenAIClientOptions { Transport = transport }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

        return ModelDefaultsChatClient.Apply(
            FabrCoreChatClientService.CreateChatClient(providerClient, configuration),
            configuration);
    }
#pragma warning restore OPENAI001

    private sealed class RecordingProviderHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
