using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using FabrCore.Core.Skills;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class FabrCoreHostApiClientTests
{
    [TestMethod]
    public async Task GetPrincipalsAsync_CallsPrincipalsEndpointWithStatusFilter()
    {
        var handler = new RecordingHandler("""
            {
              "count": 1,
              "principals": [
                {
                  "key": "user1",
                  "agentType": "Principal",
                  "handle": "user1",
                  "status": 0,
                  "activatedAt": "2026-07-05T00:00:00Z",
                  "deactivatedAt": null,
                  "deactivationReason": null,
                  "entityType": 1
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCore:HostUrl"] = "https://fabrcore.test"
            })
            .Build();
        var apiClient = new FabrCoreHostApiClient(
            httpClient,
            configuration,
            NullLogger<FabrCoreHostApiClient>.Instance);

        var response = await apiClient.GetPrincipalsAsync("active");

        Assert.AreEqual("https://fabrcore.test/fabrcoreapi/Diagnostics/principals?status=active", handler.RequestUri?.ToString());
        Assert.AreEqual(1, response.Count);
        Assert.AreEqual("user1", response.Principals.Single().Handle);
    }

    [TestMethod]
    public async Task PublishHarnessSkillAsync_StreamsZipToPrincipalAdminEndpoint()
    {
        var handler = new SkillRecordingHandler();
        var apiClient = new FabrCoreHostApiClient(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCore:HostUrl"] = "https://fabrcore.test"
            }).Build(),
            NullLogger<FabrCoreHostApiClient>.Instance);
        await using var zip = new MemoryStream([1, 2, 3, 4]);

        var result = await apiClient.PublishHarnessSkillAsync(
            "owner@example.com",
            "policy-review",
            "1.2.0",
            zip);

        Assert.AreEqual(HttpMethod.Put, handler.Method);
        Assert.AreEqual(
            "https://fabrcore.test/fabrcoreapi/admin/v1/principals/owner%40example.com/skills/policy-review/versions/1.2.0",
            handler.RequestUri?.ToString());
        Assert.AreEqual("application/zip", handler.ContentType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, handler.Body);
        Assert.AreEqual("policy-review", result.Manifest.Name);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public RecordingHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class SkillRecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"manifest":{"name":"policy-review","version":"1.2.0"},"alreadyExisted":false}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
