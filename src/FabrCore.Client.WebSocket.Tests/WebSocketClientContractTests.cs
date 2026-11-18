using FabrCore.Client.WebSocket;

namespace FabrCore.Client.WebSocket.Tests;

[TestClass]
public sealed class WebSocketClientContractTests
{
    [TestMethod]
    public async Task InMemoryCheckpoint_IsMonotonic()
    {
        var store = new InMemoryFabrCoreWebSocketCheckpointStore();
        await store.SetCheckpointAsync("desktop", 12);
        await store.SetCheckpointAsync("desktop", 7);

        Assert.AreEqual(12, await store.GetCheckpointAsync("desktop"));
    }

    [TestMethod]
    public void ClientContract_HasNoAgentCreationMethod()
    {
        var names = typeof(IFabrCoreWebSocketClient).GetMethods().Select(method => method.Name).ToArray();
        Assert.IsFalse(names.Any(name => name.Contains("Create", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(names.Any(name => name.Contains("Provision", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow(System.Net.HttpStatusCode.Unauthorized)]
    [DataRow(System.Net.HttpStatusCode.Forbidden)]
    public async Task Connect_RefusedTicket_FailsInsteadOfRetrying(System.Net.HttpStatusCode status)
    {
        var tickets = 0;
        using var http = new HttpClient(new Respond(_ =>
        {
            Interlocked.Increment(ref tickets);
            return new HttpResponseMessage(status);
        }));
        await using var client = new FabrCoreWebSocketClient(http, new FabrCoreWebSocketClientOptions
        {
            HostUri = new Uri("https://example.test"),
            ClientId = "desktop",
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refused = await Assert.ThrowsExactlyAsync<FabrCoreWebSocketException>(() => client.ConnectAsync(timeout.Token));

        Assert.AreEqual("refused", refused.Code);
        Assert.IsFalse(refused.Retryable);
        Assert.AreEqual(1, tickets);
    }

    [TestMethod]
    public void Client_RequiresStableClientId()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new FabrCoreWebSocketClient(
            new HttpClient(),
            new FabrCoreWebSocketClientOptions
            {
                HostUri = new Uri("https://example.test"),
                ClientId = " ",
            }));
    }
}

internal sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
