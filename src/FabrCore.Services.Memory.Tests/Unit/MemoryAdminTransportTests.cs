using System.Net;
using System.Text;
using FabrCore.Services.Memory.Administration;
using FabrCore.Services.Memory.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryAdminTransportTests
{
    [TestMethod]
    public async Task RemoteClientUsesVersionedEndpointAndPrincipalHeader()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"availability":0,"apiVersion":"1","features":["dashboard"]}""",
                Encoding.UTF8,
                "application/json")
        });
        var client = new RemoteMemoryAdminClient(
            new HttpClient(handler),
            Options.Create(new MemoryAdminClientOptions
            {
                BaseAddress = "https://cluster.example/",
                ApiKey = "test-cluster-key"
            }),
            new FixedPrincipalAccessor("operator1"),
            NullLogger<RemoteMemoryAdminClient>.Instance);

        var capability = await client.GetCapabilityAsync();

        Assert.IsTrue(capability.IsAvailable);
        Assert.AreEqual(
            "https://cluster.example/fabrcoreapi/memory/admin/v1/capabilities",
            handler.Request?.RequestUri?.ToString());
        Assert.AreEqual(
            "operator1",
            handler.Request?.Headers.GetValues("x-user-handle").Single());
        Assert.AreEqual("Bearer", handler.Request?.Headers.Authorization?.Scheme);
        Assert.AreEqual("test-cluster-key", handler.Request?.Headers.Authorization?.Parameter);
    }

    [TestMethod]
    public async Task RemoteCapabilityReportsUnregisteredForMissingEndpoint()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new RemoteMemoryAdminClient(
            new HttpClient(handler),
            Options.Create(new MemoryAdminClientOptions
            {
                BaseAddress = "https://cluster.example",
                ApiKey = "test-cluster-key"
            }),
            new FixedPrincipalAccessor("operator1"),
            NullLogger<RemoteMemoryAdminClient>.Instance);

        var capability = await client.GetCapabilityAsync();

        Assert.AreEqual(MemoryAdminAvailability.Unregistered, capability.Availability);
    }

    private sealed class FixedPrincipalAccessor(string principal) : IMemoryAdminPrincipalAccessor
    {
        public ValueTask<string?> GetPrincipalIdAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(principal);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(responder(request));
        }
    }
}
