using FabrCore.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ModelConfigurationResolutionTests
{
    [TestMethod]
    public async Task ResolverAwareService_CreatesClientWithoutHostHttpLookup()
    {
        var resolver = new RecordingResolver();
        var service = new FabrCoreChatClientService(
            new ConfigurationBuilder().Build(),
            NullLoggerFactory.Instance,
            resolver);

        var client = await service.GetChatClient("default");

        Assert.IsNotNull(client);
        Assert.AreEqual(1, resolver.ModelRequests);
        Assert.AreEqual(1, resolver.ApiKeyRequests);
    }

    [TestMethod]
    public async Task RemoteApiKeyLookup_SendsConfiguredAdminBearerToken()
    {
        var handler = new ResponseHandler(_ => JsonResponse("{\"value\":\"provider-key\"}"));
        var client = CreateHostApiClient(handler, adminApiKey: "admin-key");

        var result = await client.GetApiKeyAsync("provider");

        Assert.AreEqual("provider-key", result.Value);
        Assert.AreEqual("Bearer", handler.LastAuthorization?.Scheme);
        Assert.AreEqual("admin-key", handler.LastAuthorization?.Parameter);
        Assert.AreEqual("application/json", handler.LastAccept?.MediaType);
    }

    [TestMethod]
    public async Task RemoteModelLookup_MapsAllModelDefaults()
    {
        var handler = new ResponseHandler(_ => JsonResponse(
            """
            {
              "name":"default","provider":"OpenAI","uri":"https://openai.test/v1",
              "model":"gpt-test","apiKeyAlias":"provider","timeoutSeconds":90,
              "maxOutputTokens":1000,"reasoningEffort":"high","contextWindowTokens":128000,
              "contextCompactionEnabled":true,"contextEvictThreshold":0.5,
              "contextTruncateThreshold":0.8,"compactionEnabled":true,
              "compactionKeepLastN":12,"compactionThreshold":0.87,
              "compactionStaleAfterMinutes":30,"perTurnMaxInputTokens":120000,
              "maxPromptInputTokens":100000,"runawayBudgetBehavior":"StopWithDiagnostic"
            }
            """));
        IFabrCoreModelConfigurationResolver resolver = CreateHostApiClient(handler, "admin-key");

        var result = await resolver.GetModelConfigurationAsync("default");

        Assert.AreEqual(90, result.TimeoutSeconds);
        Assert.AreEqual(1000, result.MaxOutputTokens);
        Assert.AreEqual("high", result.ReasoningEffort);
        Assert.AreEqual(128000, result.ContextWindowTokens);
        Assert.AreEqual(true, result.ContextCompactionEnabled);
        Assert.AreEqual(0.5, result.ContextEvictThreshold);
        Assert.AreEqual(0.8, result.ContextTruncateThreshold);
        Assert.AreEqual(true, result.CompactionEnabled);
        Assert.AreEqual(12, result.CompactionKeepLastN);
        Assert.AreEqual(0.87, result.CompactionThreshold);
        Assert.AreEqual(30, result.CompactionStaleAfterMinutes);
        Assert.AreEqual(120000, result.PerTurnMaxInputTokens);
        Assert.AreEqual(100000, result.MaxPromptInputTokens);
        Assert.AreEqual("StopWithDiagnostic", result.RunawayBudgetBehavior);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task RemoteLookup_NonSuccessStatusIsActionableAndDoesNotExposeBody(HttpStatusCode statusCode)
    {
        const string sensitiveBody = "sensitive-response-body";
        var handler = new ResponseHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(sensitiveBody, Encoding.UTF8, "text/plain")
        });
        var client = CreateHostApiClient(handler, "admin-key");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetModelConfigAsync("missing"));

        Assert.AreEqual(statusCode, exception.StatusCode);
        StringAssert.Contains(exception.Message, ((int)statusCode).ToString());
        StringAssert.Contains(exception.Message, "text/plain");
        Assert.IsFalse(exception.Message.Contains(sensitiveBody, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RemoteLookup_RedirectReportsLocationWithoutFollowingIt()
    {
        var handler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://identity.test/login") },
            Content = new StringContent("login", Encoding.UTF8, "text/html")
        });
        var client = CreateHostApiClient(handler, "admin-key");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetModelConfigAsync("default"));

        StringAssert.Contains(exception.Message, "302");
        StringAssert.Contains(exception.Message, "https://identity.test/login");
        StringAssert.Contains(exception.Message, FabrCoreConfigurationKeys.AdminApiKey);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task RemoteApiKeyLookup_MissingAliasProducesExplicit404WithoutExposingBody()
    {
        const string sensitiveBody = "missing-provider-secret-details";
        var handler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(sensitiveBody, Encoding.UTF8, "text/plain")
        });
        var client = CreateHostApiClient(handler, "admin-key");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetApiKeyAsync("missing-key"));

        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
        StringAssert.Contains(exception.Message, "missing-key");
        Assert.IsFalse(exception.Message.Contains(sensitiveBody, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RemoteLookup_HtmlSuccessReportsAuthenticationPageWithoutExposingBody()
    {
        const string sensitiveBody = "<html>sensitive-login-page</html>";
        var handler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://identity.test/login"),
            Content = new StringContent(sensitiveBody, Encoding.UTF8, "text/html")
        });
        var client = CreateHostApiClient(handler, "admin-key");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetModelConfigAsync("default"));

        StringAssert.Contains(exception.Message, "text/html");
        StringAssert.Contains(exception.Message, "authentication or login page");
        StringAssert.Contains(exception.Message, "https://identity.test/login");
        Assert.IsFalse(exception.Message.Contains(sensitiveBody, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RemoteLookup_MalformedJsonIsWrappedWithoutExposingBody()
    {
        const string malformedBody = "{ sensitive-json";
        var handler = new ResponseHandler(_ => JsonResponse(malformedBody));
        var client = CreateHostApiClient(handler, "admin-key");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetModelConfigAsync("default"));

        StringAssert.Contains(exception.Message, "malformed JSON");
        Assert.IsInstanceOfType<System.Text.Json.JsonException>(exception.InnerException);
        Assert.IsFalse(exception.Message.Contains(malformedBody, StringComparison.Ordinal));
    }

    private static FabrCoreHostApiClient CreateHostApiClient(
        HttpMessageHandler handler,
        string? adminApiKey)
    {
        var settings = new Dictionary<string, string?>
        {
            [FabrCoreConfigurationKeys.HostUrl] = "https://fabrcore.test",
            [FabrCoreConfigurationKeys.AdminApiKey] = adminApiKey
        };
        return new FabrCoreHostApiClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<FabrCoreHostApiClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public MediaTypeWithQualityHeaderValue? LastAccept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastAuthorization = request.Headers.Authorization;
            LastAccept = request.Headers.Accept.SingleOrDefault();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RecordingResolver : IFabrCoreModelConfigurationResolver
    {
        public int ModelRequests { get; private set; }
        public int ApiKeyRequests { get; private set; }

        public Task<ModelConfiguration> GetModelConfigurationAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ModelRequests++;
            return Task.FromResult(new ModelConfiguration
            {
                Name = name,
                Provider = "OpenAI",
                Uri = "https://openai.test/v1",
                Model = "gpt-test",
                ApiKeyAlias = "provider"
            });
        }

        public Task<string> GetApiKeyAsync(
            string alias,
            CancellationToken cancellationToken = default)
        {
            ApiKeyRequests++;
            return Task.FromResult("provider-key");
        }
    }
}
