using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests;
[TestClass]
public sealed class CloudAdministrationQueryTests
{
    [TestMethod]
    public async Task InvalidAndRepurposedMonitorCursorsAreRejected()
    {
        var monitor = new InMemoryAgentMessageMonitor(NullLogger<InMemoryAgentMessageMonitor>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.QueryAsync(new() { Cursor = "bnVsbA==" }));
        var first = await monitor.QueryAsync(new() { Principal = "alice" });
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.QueryAsync(new() { Principal = "bob", Cursor = first.NextCursor }));
    }
    [TestMethod]
    public void ReservedChannelsCannotEnterOrdinaryRuntime()
    {
        foreach (var channel in new[] { "_admin", "_ADMIN", "_debug" }) Assert.Throws<UnauthorizedAccessException>(() => FabrCoreAdminChannels.RejectOrdinary(channel));
        FabrCoreAdminChannels.RejectOrdinary("chat");
        FabrCoreAdminChannels.RejectOrdinary(null);
    }
    [TestMethod]
    public async Task MonitorFiltersBeforePagingAndTraversesBeyondOneThousand()
    {
        var monitor = new InMemoryAgentMessageMonitor(NullLogger<InMemoryAgentMessageMonitor>.Instance, 5000);
        for (var i = 0; i < 2400; i++) await monitor.RecordMessageAsync(new() { AgentHandle = i % 2 == 0 ? "a:agent" : "b:agent", Message = i.ToString() });
        var q = new MonitorQuery { Principal = "a", Limit = 100 };
        var count = 0; var ids = new HashSet<string>();
        MonitorPage page;
        do
        {
            page = await monitor.QueryAsync(q); q.Cursor = page.NextCursor;
            Assert.IsFalse(page.Gap);
            foreach (var item in page.Items) { Assert.AreEqual("a:agent", item.AgentHandle); Assert.IsTrue(ids.Add(item.Id)); }
            count += page.Items.Count;
        } while (page.HasMore);
        Assert.AreEqual(1200, count);
        await monitor.RecordMessageAsync(new() { AgentHandle = "a:agent", Message = "new" });
        Assert.AreEqual(1, (await monitor.QueryAsync(q)).Items.Count);
    }
    [TestMethod]
    public async Task MonitorCursorReportsEvictionAndClear()
    {
        var monitor = new InMemoryAgentMessageMonitor(NullLogger<InMemoryAgentMessageMonitor>.Instance, 1);
        await monitor.RecordMessageAsync(new() { AgentHandle = "a:agent" });
        var cursor = (await monitor.QueryAsync(new())).NextCursor;
        for (var i = 0; i < 10; i++) await monitor.RecordMessageAsync(new() { AgentHandle = "a:agent" });
        Assert.IsTrue((await monitor.QueryAsync(new() { Cursor = cursor })).Gap);
        await monitor.ClearAsync();
        Assert.IsTrue((await monitor.QueryAsync(new() { Cursor = cursor })).Gap);
        Assert.AreEqual(0, (await monitor.QueryAsync(new())).Items.Count);
    }
}
