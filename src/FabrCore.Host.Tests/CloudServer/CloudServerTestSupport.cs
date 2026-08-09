using FabrCore.Host.Configuration;
using FabrCore.Host.Services.CloudServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace FabrCore.Host.Tests.CloudServer;

internal sealed class FakeCloudServerHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

    public FakeCloudServerHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : this((request, _) => responder(request))
    {
    }

    public FakeCloudServerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        this.responder = responder;
    }

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return await responder(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, object body) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web))
    };
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class TestHostEnvironment(string contentRootPath, string environmentName = "Test") : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "FabrCore.Host.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = environmentName;
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal static class CloudServerTestFactory
{
    public static CloudServerOptions Options(Action<CloudServerOptions>? configure = null)
    {
        var options = new CloudServerOptions
        {
            Enabled = true,
            Url = "https://forge.test",
            ApiKey = "frg_test_key",
            RequestTimeout = TimeSpan.FromSeconds(5)
        };
        configure?.Invoke(options);
        return options;
    }

    public static RemoteAdministrationOptions RemoteOptions(
        Action<RemoteAdministrationOptions>? configure = null)
    {
        var options = new RemoteAdministrationOptions();
        configure?.Invoke(options);
        return options;
    }

    public static CloudServerApiClient ApiClient(
        HttpMessageHandler handler,
        CloudServerOptions options,
        RemoteAdministrationOptions? remoteAdministration = null,
        string environmentName = "Test",
        Dictionary<string, string?>? configValues = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? [])
            .Build();
        return new CloudServerApiClient(
            new FakeHttpClientFactory(handler),
            new CloudServerConnectClient(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(remoteAdministration ?? RemoteOptions()),
            configuration,
            new TestHostEnvironment(Path.GetTempPath(), environmentName),
            NullLogger<CloudServerApiClient>.Instance);
    }
}
