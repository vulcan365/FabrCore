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
