using System.Reflection;
using System.Text;
using System.Text.Json;
using FabrCore.Host.A2A;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FabrCore.Host.Testing;

/// <summary>
/// Stands up a FabrCore host's A2A endpoints over an in-memory test server, so an application can
/// test its own exposure end to end without an Orleans silo.
/// </summary>
/// <remarks>
/// <para>
/// This drives the real routes, the real authentication handlers, and the real wire format — which
/// is where A2A interop actually breaks. Testing the handler in isolation, or asserting that two
/// configuration blocks match, does not catch a caller landing on a different agent grain than you
/// intended; that is only observable at this seam.
/// </para>
/// <para>
/// The agent and registry services are fakes, so nothing reaches a silo. Everything else — route
/// mapping, credential checks, principal resolution, card generation — is the shipped code.
/// </para>
/// <example>
/// <code>
/// await using var host = await FabrCoreA2ATestHost.StartAsync(new Dictionary&lt;string, string?&gt;
/// {
///     ["A2A:Enabled"] = "true",
///     ["A2A:Authentication:Mode"] = "None",
///     ["A2A:AgentTypes:0"] = "support-agent",
/// });
///
/// using var card = await host.GetJsonAsync("/a2a/support-agent/.well-known/agent-card.json");
/// Assert.AreEqual("Support Agent", card.RootElement.GetProperty("name").GetString());
/// </code>
/// </example>
/// </remarks>
public sealed class FabrCoreA2ATestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FabrCoreA2ATestHost(WebApplication app, FakeFabrCoreAgentService agentService)
    {
        _app = app;
        AgentService = agentService;
        Client = app.GetTestClient();
    }

    /// <summary>An <see cref="HttpClient"/> bound to the in-memory server.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// The fake agent service. Assert against <see cref="FakeFabrCoreAgentService.Sends"/> to see
    /// which principal and handle a call actually reached.
    /// </summary>
    public FakeFabrCoreAgentService AgentService { get; }

    /// <summary>Starts a host with the supplied <c>A2A:*</c> configuration.</summary>
    /// <param name="configuration">Configuration entries, in <c>A2A:Section:Key</c> form.</param>
    /// <param name="agentService">Fake agent service. A new one is created when omitted.</param>
    /// <param name="registry">
    /// Agent-type registry. Defaults to an empty <see cref="FakeFabrCoreRegistry"/>; pass
    /// <see cref="RegistryFor"/> to scan real <c>[AgentAlias]</c> types from your own assemblies.
    /// </param>
    /// <param name="configureServices">
    /// Runs before the host registers its own A2A services, which use <c>TryAdd</c>. This is where
    /// to register a custom <see cref="A2A.IA2APrincipalResolver"/>, <see cref="A2A.IA2ATaskStore"/>,
    /// or other replacement — registering after this point is silently ignored.
    /// </param>
    public static async Task<FabrCoreA2ATestHost> StartAsync(
        IDictionary<string, string?> configuration,
        FakeFabrCoreAgentService? agentService = null,
        IFabrCoreRegistry? registry = null,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(configuration);

        agentService ??= new FakeFabrCoreAgentService();
        builder.Services.AddSingleton<IFabrCoreAgentService>(agentService);
        builder.Services.AddSingleton(registry ?? new FakeFabrCoreRegistry());
        builder.Services.TryAddSingleton(TimeProvider.System);

        // AddFabrCoreServer supplies these in a real host.
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        // Before AddA2A, so a caller's own replacements win the TryAdd.
        configureServices?.Invoke(builder.Services);

        builder.AddA2A();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseA2A();

        await app.StartAsync();
        return new FabrCoreA2ATestHost(app, agentService);
    }

    /// <summary>Builds a real registry that scans <paramref name="assemblies"/> for agent types.</summary>
    public static IFabrCoreRegistry RegistryFor(params Assembly[] assemblies)
        => new FabrCoreRegistry(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FabrCoreRegistry>.Instance,
            assemblies);

    /// <summary>GETs a path and parses the JSON body, throwing on a non-success status.</summary>
    public async Task<JsonDocument> GetJsonAsync(string path, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddKey(request, apiKey);
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>POSTs a JSON body and returns the raw response, for status-code assertions.</summary>
    public Task<HttpResponseMessage> PostJsonAsync(string path, string json, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddKey(request, apiKey);
        return Client.SendAsync(request);
    }

    /// <summary>
    /// POSTs a JSON body requesting <c>text/event-stream</c> and returns each SSE <c>data:</c>
    /// frame, parsed. Blocks until the stream ends.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ReadServerSentEventsAsync(
        string path, string json, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        AddKey(request, apiKey);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = new List<JsonElement>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
            }
        }

        return events;
    }

    /// <summary>A JSON-RPC <c>message/send</c> body, the shape most A2A calls take.</summary>
    public static string MessageSendRequest(string text, string? contextId = null, object? id = null)
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id ?? 1,
            method = "message/send",
            @params = new
            {
                message = new
                {
                    kind = "message",
                    role = "user",
                    messageId = Guid.NewGuid().ToString(),
                    contextId,
                    parts = new[] { new { kind = "text", text } },
                },
            },
        });

    private static void AddKey(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("x-api-key", apiKey);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
