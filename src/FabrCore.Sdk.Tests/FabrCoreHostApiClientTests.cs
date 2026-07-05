using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class FabrCoreHostApiClientTests
{
    [TestMethod]
    public async Task GetUsersAsync_CallsUsersEndpointWithStatusFilter()
    {
        var handler = new RecordingHandler("""
            {
              "count": 1,
              "users": [
                {
                  "key": "user1",
                  "agentType": "Client",
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
                ["FabrCoreHostUrl"] = "https://fabrcore.test"
            })
            .Build();
        var apiClient = new FabrCoreHostApiClient(
            httpClient,
            configuration,
            NullLogger<FabrCoreHostApiClient>.Instance);

        var response = await apiClient.GetUsersAsync("active");

        Assert.AreEqual("https://fabrcore.test/fabrcoreapi/Diagnostics/users?status=active", handler.RequestUri?.ToString());
        Assert.AreEqual(1, response.Count);
        Assert.AreEqual("user1", response.Users.Single().Handle);
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
}
